using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace AbpAi;

/// <summary>A call about to leave: what was asked, where it goes, its class and its estimate.</summary>
public sealed record AiCall(AiRequest Request, AiScopeChain Chain, AiRoute Route, string DataClass, decimal Estimate, bool Streamed);

/// <summary>A limit a call was checked against, with what was spent before it.</summary>
public sealed record AiCheckedLimit(AiLimitRef Limit);

/// <summary>A call's estimate held on the ledger, and the limits it was checked against.</summary>
public sealed record AiReservation(Guid UsageId, IReadOnlyList<AiCheckedLimit> Limits);

/// <summary>
/// The usage ledger. Before a call leaves, its estimate is reserved under one lock, so calls at the same time can't
/// go over a limit together: the call must fit its scope's total and every total above it (the site's too), the
/// named limits of the accounts in it, and what its person may spend at each of those levels. After it answers, the
/// record is settled with what it cost, and admins hear when a limit passes its notice mark.
/// </summary>
public sealed class AiLedger(AiDb aiDb, IAiAccounts accounts, IAiNotices notices, IOptions<AiOptions> options, ILogger<AiLedger> logger) : ITransientDependency
{
    /// <summary>The ledger's lock (a Postgres advisory lock): "AI LEDGR" in hex.</summary>
    public const long LockKey = 0x4149_204C_4544_4752;

    public async Task<AiReservation> ReserveAsync(AiCall call, CancellationToken ct)
    {
        AiRequest request = call.Request;
        Guid? person = request.OnBehalfOf ?? request.AccountId;
        Guid? account = request.AccountId != person ? request.AccountId : null;
        IReadOnlyList<Guid> groups = person is null ? [] : await accounts.GroupsOfAsync(person.Value, ct);
        DateTime now = DateTime.UtcNow;
        DateTime liveSince = now - options.Value.ReservationTimeout;
        List<Guid> ids = [.. call.Chain.Up];
        List<Guid?> levels = [.. call.Chain.Up.Select(id => (Guid?)id), null];

        return await aiDb.RunAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", [LockKey], ct);

            List<AiLimit> set = await db.AiLimits.AsNoTracking().Where(l => l.ScopeId == null || ids.Contains(l.ScopeId.Value)).ToListAsync(ct);
            List<(AiLimit Limit, Guid? Whose)> applicable = [];
            foreach (Guid? level in levels)
            {
                foreach (string period in AiPeriods.All)
                {
                    List<AiLimit> here = [.. set.Where(l => l.ScopeId == level && l.Period == period)];
                    applicable.AddRange(here.Where(l => l.Kind == AiLimitKinds.Total).Select(l => (l, (Guid?)null)));
                    if (person is not null)
                    {
                        AiLimit? own = here.FirstOrDefault(l => l.Kind == AiLimitKinds.Account && l.AccountId == person)
                            ?? here.Where(l => l.Kind == AiLimitKinds.Group && l.GroupId is not null && groups.Contains(l.GroupId.Value)).MaxBy(l => l.Amount)
                            ?? here.FirstOrDefault(l => l.Kind == AiLimitKinds.Person);
                        if (own is not null)
                        {
                            applicable.Add((own, person));
                        }
                    }

                    if (account is not null && here.FirstOrDefault(l => l.Kind == AiLimitKinds.Account && l.AccountId == account) is { } named)
                    {
                        applicable.Add((named, account));
                    }
                }
            }

            List<AiCheckedLimit> checkedLimits = [];
            AiLimitRef? over = null;
            foreach ((AiLimit limit, Guid? whose) in applicable)
            {
                (DateTime start, DateTime end) = AiPeriods.Of(limit.Period, now);
                IQueryable<AiUsageRecord> counted = db.AiUsage.AsNoTracking().Where(r => r.At >= start
                    && (r.State == AiUsageStates.Done || (r.State == AiUsageStates.Reserved && r.At >= liveSince)));
                if (limit.ScopeId is { } scope)
                {
                    counted = counted.Where(r => r.ScopePath.Contains(scope));
                }

                if (whose is { } who)
                {
                    counted = counted.Where(r => r.AccountId == who || r.OnBehalfOf == who);
                }

                decimal spent = await counted.SumAsync(r => r.Cost, ct);
                AiLimitRef found = new(limit.Id, KindOf(limit), limit.ScopeId, whose, limit.Period, limit.Amount, spent, end);
                checkedLimits.Add(new AiCheckedLimit(found));
                if (spent + call.Estimate > limit.Amount && (over is null || found.FreesAt > over.FreesAt))
                {
                    over = found;
                }
            }

            if (over is not null)
            {
                throw new AiRefusedException(AiRefusalCodes.OverLimit, Describe(over), over);
            }

            AiUsageRecord record = new(Guid.NewGuid(), now, call.Chain.Scope, call.Chain.Path, request.AccountId, request.OnBehalfOf, request.Purpose, call.Route.Alias,
                call.Route.Connection.Id, call.Route.Connection.Name, call.Route.Connection.Provider, call.Route.Model, call.DataClass, call.Estimate, call.Streamed)
            {
                AboutKind = request.About?.Kind,
                AboutId = request.About?.Id,
            };
            db.AiUsage.Add(record);
            await db.SaveChangesAsync(ct);
            return new AiReservation(record.Id, checkedLimits);
        }, requiresNew: true, cancellationToken: ct, isTransactional: true);
    }

    /// <summary>Settles a call with what it cost, and tells admins of each limit it took past the notice mark.</summary>
    public async Task SettleAsync(AiReservation reservation, AiCall call, int inputTokens, int outputTokens, decimal cost, int durationMs, CancellationToken ct)
    {
        await aiDb.RunAsync(async db =>
        {
            AiUsageRecord record = await db.AiUsage.FirstAsync(r => r.Id == reservation.UsageId, ct);
            record.State = AiUsageStates.Done;
            record.InputTokens = inputTokens;
            record.OutputTokens = outputTokens;
            record.Cost = cost;
            record.DurationMs = durationMs;
            await db.SaveChangesAsync(ct);
        }, requiresNew: true, cancellationToken: ct);

        decimal mark = options.Value.NoticeAt;
        foreach (AiCheckedLimit limit in reservation.Limits)
        {
            decimal at = limit.Limit.Amount * mark;
            if (limit.Limit.Amount > 0 && limit.Limit.Spent < at && limit.Limit.Spent + cost >= at)
            {
                try
                {
                    await notices.LimitNearingAsync(new AiLimitNotice(limit.Limit with { Spent = limit.Limit.Spent + cost }, call.Chain.Scope, call.Request.AccountId,
                        call.Request.Purpose), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Telling admins that limit {Limit} nears failed.", limit.Limit.LimitId);
                }
            }
        }
    }

    /// <summary>The call failed or was stopped: it costs nothing, and its error is kept.</summary>
    public Task FailAsync(AiReservation reservation, string error, int durationMs) =>
        aiDb.RunAsync(async db =>
        {
            AiUsageRecord? record = await db.AiUsage.FirstOrDefaultAsync(r => r.Id == reservation.UsageId);
            if (record is not null)
            {
                record.State = AiUsageStates.Failed;
                record.Cost = 0;
                record.DurationMs = durationMs;
                record.Error = error.Length > 1000 ? error[..1000] : error;
                await db.SaveChangesAsync();
            }
        }, requiresNew: true);

    internal static string KindOf(AiLimit limit) => limit.Kind switch
    {
        AiLimitKinds.Total => "scope",
        AiLimitKinds.Account => "account",
        _ => "person",
    };

    private static string Describe(AiLimitRef limit)
    {
        string where = limit.ScopeId is null ? "the site's" : "this scope's";
        string every = limit.Period switch { AiPeriods.Day => "daily", AiPeriods.Week => "weekly", _ => "monthly" };
        string what = limit.Kind switch
        {
            "scope" => $"{where} {every} AI limit",
            "account" => $"this account's {every} AI limit on {where[..^2]}",
            _ => $"your {every} AI limit on {where[..^2]}",
        };
        return $"{char.ToUpperInvariant(what[0])}{what[1..]} is reached ({limit.Spent:0.####} of {limit.Amount:0.####}). It frees at {limit.FreesAt:yyyy-MM-dd HH:mm} UTC.";
    }
}
