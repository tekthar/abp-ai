using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace AbpAi;

/// <summary>A call about to leave: what was asked, where it goes, its class and its estimate.</summary>
/// <param name="Estimate">What it may cost at most, in price units.</param>
public sealed record AiCall(AiRequest Request, AiScopeChain Chain, AiRoute Route, string DataClass, decimal Estimate, bool Streamed)
{
    /// <summary>The tokens it may take at most: its text's in, and all of its <c>MaxTokens</c> out.</summary>
    public int EstimatedTokens { get; init; }
}

/// <summary>What a call took: its tokens in (those the provider didn't cache or read from its cache), written to the cache, read from it, and out.</summary>
public sealed record AiSettlement(int InputTokens, int OutputTokens, int CacheWriteTokens, int CacheReadTokens)
{
    /// <summary>Every token it took, as token limits count them.</summary>
    public long All => (long)InputTokens + CacheWriteTokens + CacheReadTokens + OutputTokens;
}

/// <summary>A limit a call was checked against, with what was spent before it.</summary>
public sealed record AiCheckedLimit(AiLimitRef Limit);

/// <summary>A call's estimate held on the ledger, and the limits it was checked against.</summary>
public sealed record AiReservation(Guid UsageId, IReadOnlyList<AiCheckedLimit> Limits);

/// <summary>
/// The usage ledger. Before a call leaves, its estimate is reserved under one lock, so calls at the same time can't
/// go over a limit together: the call must fit its scope's total and every total above it (the site's too), the
/// named limits of the accounts in it, and what its person may spend at each of those levels, in each measure (cost,
/// tokens, calls) and period. After it answers, the record is settled with what it cost, and admins hear when a limit
/// passes its notice mark.
/// </summary>
public sealed class AiLedger(AiDb aiDb, IAiAccounts accounts, IAiNotices notices, IOptions<AiOptions> options, ILogger<AiLedger> logger) : ITransientDependency
{
    /// <summary>The ledger's lock (a Postgres advisory lock): "AI LEDGR" in hex.</summary>
    public const long LockKey = 0x4149_204C_4544_4752;

    /// <summary>
    /// The most tokens a reply may have for the call: the lowest <see cref="AiLimitKinds.CallTokens"/> on the site, on its
    /// scope or one above it, or on its account or the person it's for; null when none is set.
    /// </summary>
    public async Task<int?> CallTokensAsync(AiRequest request, AiScopeChain chain, CancellationToken ct)
    {
        Guid? account = request.AccountId, person = request.OnBehalfOf ?? request.AccountId;
        List<Guid> ids = [.. chain.Up];
        List<decimal> caps = await aiDb.RunAsync(db => db.AiLimits.AsNoTracking()
            .Where(l => l.Kind == AiLimitKinds.CallTokens && (l.ScopeId == null || ids.Contains(l.ScopeId.Value))
                && (l.AccountId == null || l.AccountId == account || l.AccountId == person))
            .Select(l => l.Amount).ToListAsync(ct), cancellationToken: ct);
        return caps.Count == 0 ? null : (int)Math.Clamp(caps.Min(), 1, int.MaxValue);
    }

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

            List<AiLimit> set = await db.AiLimits.AsNoTracking().Where(l => l.Kind != AiLimitKinds.CallTokens && (l.ScopeId == null || ids.Contains(l.ScopeId.Value)))
                .ToListAsync(ct);
            List<(AiLimit Limit, Guid? Whose)> applicable = [];
            foreach (Guid? level in levels)
            {
                foreach (string period in AiPeriods.All)
                {
                    foreach (string measure in AiLimitMeasures.All)
                    {
                        List<AiLimit> here = [.. set.Where(l => l.ScopeId == level && l.Period == period && l.Measure == measure)];
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
            }

            List<AiCheckedLimit> checkedLimits = [];
            AiLimitRef? over = null;
            foreach ((AiLimit limit, Guid? whose) in applicable)
            {
                (DateTime start, DateTime end) = AiPeriods.Of(limit.Period, now);
                // What answered, what's running, and a stream that failed halfway, at what it used.
                IQueryable<AiUsageRecord> counted = db.AiUsage.AsNoTracking().Where(r => r.At >= start
                    && (r.State == AiUsageStates.Done || (r.State == AiUsageStates.Reserved && r.At >= liveSince)
                        || (r.State == AiUsageStates.Failed && (r.Cost > 0 || r.InputTokens > 0 || r.OutputTokens > 0))));
                if (limit.ScopeId is { } scope)
                {
                    counted = counted.Where(r => r.ScopePath.Contains(scope));
                }

                if (whose is { } who)
                {
                    counted = counted.Where(r => r.AccountId == who || r.OnBehalfOf == who);
                }

                // A reservation holds its estimate; a settled call what it took.
                decimal spent = limit.Measure switch
                {
                    AiLimitMeasures.Tokens => await counted.SumAsync(r => r.State == AiUsageStates.Reserved
                        ? (long)r.ReservedTokens
                        : (long)r.InputTokens + r.OutputTokens + r.CacheWriteTokens + r.CacheReadTokens, ct),
                    AiLimitMeasures.Requests => await counted.CountAsync(r => r.State != AiUsageStates.Failed, ct),
                    _ => await counted.SumAsync(r => r.Cost, ct),
                };
                decimal adds = limit.Measure switch
                {
                    AiLimitMeasures.Tokens => call.EstimatedTokens,
                    AiLimitMeasures.Requests => 1,
                    _ => call.Estimate,
                };
                AiLimitRef found = new(limit.Id, KindOf(limit), limit.ScopeId, whose, limit.Period, limit.Amount, spent, end) { Measure = limit.Measure };
                checkedLimits.Add(new AiCheckedLimit(found));
                if (spent + adds > limit.Amount && (over is null || found.FreesAt > over.FreesAt))
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
                AboutKind = request.About?.Kind is { } kind ? AiUsageRecord.Cut(kind, 128) : null,
                AboutId = request.About?.Id is { } about ? AiUsageRecord.Cut(about, 128) : null,
                ReservedTokens = call.EstimatedTokens,
            };
            db.AiUsage.Add(record);
            await db.SaveChangesAsync(ct);
            return new AiReservation(record.Id, checkedLimits);
        }, requiresNew: true, cancellationToken: ct, isTransactional: true);
    }

    /// <summary>Settles a call with its tokens in and out, as <see cref="SettleAsync(AiReservation, AiCall, AiSettlement, decimal, int, CancellationToken)"/>.</summary>
    public Task SettleAsync(AiReservation reservation, AiCall call, int inputTokens, int outputTokens, decimal cost, int durationMs, CancellationToken ct) =>
        SettleAsync(reservation, call, new AiSettlement(inputTokens, outputTokens, 0, 0), cost, durationMs, ct);

    /// <summary>
    /// Settles a call with what it cost, and tells admins of each limit it took past the notice mark, and of each it took
    /// to its amount, in the limit's measure. A limit over a minute is a rate that frees within it, so no one is told of it.
    /// </summary>
    public async Task SettleAsync(AiReservation reservation, AiCall call, AiSettlement used, decimal cost, int durationMs, CancellationToken ct)
    {
        await aiDb.RunAsync(async db =>
        {
            AiUsageRecord record = await db.AiUsage.FirstAsync(r => r.Id == reservation.UsageId, ct);
            record.State = AiUsageStates.Done;
            record.InputTokens = used.InputTokens;
            record.OutputTokens = used.OutputTokens;
            record.CacheWriteTokens = used.CacheWriteTokens;
            record.CacheReadTokens = used.CacheReadTokens;
            record.Cost = cost;
            record.DurationMs = durationMs;
            await db.SaveChangesAsync(ct);
        }, requiresNew: true, cancellationToken: ct);

        decimal mark = options.Value.NoticeAt;
        foreach (AiCheckedLimit limit in reservation.Limits.Where(l => l.Limit.Period != AiPeriods.Minute && l.Limit.Amount > 0))
        {
            decimal took = limit.Limit.Measure switch
            {
                AiLimitMeasures.Tokens => used.All,
                AiLimitMeasures.Requests => 1,
                _ => cost,
            };
            decimal spent = limit.Limit.Spent, at = limit.Limit.Amount * mark;
            AiLimitNotice notice = new(limit.Limit with { Spent = spent + took }, call.Chain.Scope, call.Request.AccountId, call.Request.Purpose);
            if (spent < at && spent + took >= at)
            {
                try
                {
                    await notices.LimitNearingAsync(notice, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Telling admins that limit {Limit} nears failed.", limit.Limit.LimitId);
                }
            }

            if (spent < limit.Limit.Amount && spent + took >= limit.Limit.Amount)
            {
                await TellReachedAsync(notice, ct);
            }
        }
    }

    /// <summary>The call failed or was stopped: it costs nothing, and its error is kept.</summary>
    public Task FailAsync(AiReservation reservation, string error, int durationMs) => FailAsync(reservation, error, durationMs, null);

    /// <summary>The call failed or was stopped: it costs nothing, and its error and why (a <see cref="AiRefusalCodes"/>) are kept.</summary>
    public Task FailAsync(AiReservation reservation, string error, int durationMs, string? code) =>
        FailAsync(reservation, error, durationMs, code, new AiSettlement(0, 0, 0, 0), 0m);

    /// <summary>
    /// The call failed after the provider had begun to answer (a stream that stopped halfway): it costs what it had used,
    /// which the limits count in cost and tokens (not as a call), and its error and why are kept.
    /// </summary>
    public Task FailAsync(AiReservation reservation, string error, int durationMs, string? code, AiSettlement used, decimal cost) =>
        aiDb.RunAsync(async db =>
        {
            AiUsageRecord? record = await db.AiUsage.FirstOrDefaultAsync(r => r.Id == reservation.UsageId);
            if (record is not null)
            {
                record.State = AiUsageStates.Failed;
                record.Cost = cost;
                record.InputTokens = used.InputTokens;
                record.OutputTokens = used.OutputTokens;
                record.CacheWriteTokens = used.CacheWriteTokens;
                record.CacheReadTokens = used.CacheReadTokens;
                record.DurationMs = durationMs;
                record.Error = error.Length > 1000 ? error[..1000] : error;
                record.Code = code;
                await db.SaveChangesAsync();
            }
        }, requiresNew: true);

    /// <summary>
    /// A call the gateway refused before it left, kept on the ledger at no cost with why (its code and words), so usage
    /// counts refusals too; when a limit refused it, the limit's admins hear it's reached, once a period. Keeping it never
    /// fails the call: the refusal is what the caller gets.
    /// </summary>
    public async Task RefusedAsync(AiRequest request, AiScopeChain chain, string dataClass, AiRoute? route, bool streamed, AiRefusedException refused, CancellationToken ct)
    {
        try
        {
            AiUsageRecord record = new(Guid.NewGuid(), DateTime.UtcNow, chain.Scope, chain.Path, request.AccountId, request.OnBehalfOf, request.Purpose,
                (request.Alias ?? "").Trim(), route?.Connection.Id ?? Guid.Empty, route?.Connection.Name ?? "", route?.Connection.Provider ?? "", route?.Model ?? "",
                dataClass, 0m, streamed)
            {
                State = AiUsageStates.Refused,
                Code = refused.Code,
                Error = refused.Message.Length > 1000 ? refused.Message[..1000] : refused.Message,
                AboutKind = request.About?.Kind is { } kind ? AiUsageRecord.Cut(kind, 128) : null,
                AboutId = request.About?.Id is { } about ? AiUsageRecord.Cut(about, 128) : null,
                DurationMs = 0,
            };
            await aiDb.RunAsync(async db =>
            {
                db.AiUsage.Add(record);
                await db.SaveChangesAsync(CancellationToken.None);
            }, requiresNew: true, cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A refused AI call couldn't be kept on the ledger.");
        }

        if (refused.Code == AiRefusalCodes.OverLimit && refused.Limit is { } limit && limit.Period != AiPeriods.Minute)
        {
            await TellReachedAsync(new AiLimitNotice(limit, chain.Scope, request.AccountId, request.Purpose), ct);
        }
    }

    /// <summary>Tells the limit's admins it's reached, when they haven't been told in its period yet; a failure to tell never fails anything.</summary>
    private async Task TellReachedAsync(AiLimitNotice notice, CancellationToken ct)
    {
        try
        {
            DateTime start = AiPeriods.Of(notice.Limit.Period, notice.Limit.FreesAt.AddTicks(-1)).Start;
            Guid id = notice.Limit.LimitId;
            int marked = await aiDb.RunAsync(db => db.AiLimits.Where(l => l.Id == id && (l.ReachedNoticeFor == null || l.ReachedNoticeFor != start))
                .ExecuteUpdateAsync(u => u.SetProperty(l => l.ReachedNoticeFor, start), CancellationToken.None), requiresNew: true, cancellationToken: CancellationToken.None);
            if (marked > 0)
            {
                await notices.LimitReachedAsync(notice, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Telling admins that limit {Limit} is reached failed.", notice.Limit.LimitId);
        }
    }

    internal static string KindOf(AiLimit limit) => limit.Kind switch
    {
        AiLimitKinds.Total => "scope",
        AiLimitKinds.Account => "account",
        _ => "person",
    };

    private static string Describe(AiLimitRef limit)
    {
        string where = limit.ScopeId is null ? "the site's" : "this scope's";
        string every = limit.Period switch { AiPeriods.Minute => "per-minute", AiPeriods.Day => "daily", AiPeriods.Week => "weekly", _ => "monthly" };
        string of = limit.Measure switch { AiLimitMeasures.Tokens => "AI token limit", AiLimitMeasures.Requests => "AI call limit", _ => "AI limit" };
        string what = limit.Kind switch
        {
            "scope" => $"{where} {every} {of}",
            "account" => $"this account's {every} {of} on {where[..^2]}",
            _ => $"your {every} {of} on {where[..^2]}",
        };
        string amounts = limit.Measure switch
        {
            AiLimitMeasures.Tokens => $"{limit.Spent:0} of {limit.Amount:0} tokens",
            AiLimitMeasures.Requests => $"{limit.Spent:0} of {limit.Amount:0} calls",
            _ => $"{limit.Spent:0.####} of {limit.Amount:0.####}",
        };
        string frees = limit.Period == AiPeriods.Minute ? $"{limit.FreesAt:HH:mm:ss}" : $"{limit.FreesAt:yyyy-MM-dd HH:mm}";
        return $"{char.ToUpperInvariant(what[0])}{what[1..]} is reached ({amounts}). It frees at {frees} UTC.";
    }
}
