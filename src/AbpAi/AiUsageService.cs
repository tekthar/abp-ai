using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace AbpAi;

/// <summary>What to read of the ledger: when (from, before), where (a scope and everything under it; null for the whole site), and how to group.</summary>
/// <param name="GroupBy"><c>scope</c> (the scopes right under the one asked about), <c>person</c>, <c>account</c>, <c>purpose</c>, <c>model</c>, <c>connection</c>, <c>day</c>, <c>data-class</c>.</param>
public sealed record AiUsageQuery(DateTime From, DateTime To, Guid? ScopeId = null, string GroupBy = AiUsageGroups.Scope)
{
    public Guid? AccountId { get; init; }
    public string? Purpose { get; init; }
    public string? Model { get; init; }
}

public static class AiUsageGroups
{
    public const string Scope = "scope";
    public const string Person = "person";
    public const string Account = "account";
    public const string Purpose = "purpose";
    public const string Model = "model";
    public const string Connection = "connection";
    public const string Day = "day";
    public const string DataClass = "data-class";

    public static readonly IReadOnlyList<string> All = [Scope, Person, Account, Purpose, Model, Connection, Day, DataClass];
}

/// <summary>One group's calls, tokens and cost. For scopes, the key is the scope's id and the title the host's name for it.</summary>
public sealed record AiUsageRow(string Key, string? Title, int Calls, long InputTokens, long OutputTokens, decimal Cost);

public sealed record AiUsageReport(AiUsageQuery Query, IReadOnlyList<AiUsageRow> Rows, AiUsageRow Total);

/// <summary>
/// Usage people can read (AI4): by scope, person, purpose and model, and exported. The site's admins read the whole
/// site; a scope's admins read their scope and those under it, and nothing outside it; each person reads their own.
/// Only calls that answered count; failed and refused ones cost nothing.
/// </summary>
public sealed class AiUsageService(AiDb aiDb, AiAdminService admin, IAiScopes scopes, AiScopeChains chains) : ITransientDependency
{
    public const int MaxExportRows = 100_000;

    public async Task<AiUsageReport> QueryAsync(AiActor actor, AiUsageQuery query, CancellationToken ct)
    {
        await MayReadAsync(actor, query.ScopeId, ct);
        return await ReportAsync(query, r => true, ct);
    }

    /// <summary>The actor's own usage: their calls, and the calls made for them (an agent's run they started).</summary>
    public Task<AiUsageReport> MineAsync(AiActor actor, DateTime from, DateTime to, string groupBy, CancellationToken ct)
    {
        Guid me = actor.Id;
        return ReportAsync(new AiUsageQuery(from, to, null, groupBy), r => r.AccountId == me || r.OnBehalfOf == me, ct);
    }

    /// <summary>The calls themselves, as CSV: one line each, newest first, as many as <see cref="MaxExportRows"/>.</summary>
    public async Task<string> ExportCsvAsync(AiActor actor, AiUsageQuery query, CancellationToken ct)
    {
        await MayReadAsync(actor, query.ScopeId, ct);
        List<AiUsageRecord> records = await aiDb.RunAsync(db => Filter(db, query).Where(r => r.State == AiUsageStates.Done).OrderByDescending(r => r.At)
            .Take(MaxExportRows).ToListAsync(ct), cancellationToken: ct);
        IReadOnlyDictionary<Guid, AiScopeInfo> names = await scopes.GetManyAsync(records.Where(r => r.ScopeId is not null).Select(r => r.ScopeId!.Value).Distinct(), ct);
        StringBuilder csv = new("at,scope_id,scope,account_id,on_behalf_of,purpose,about_kind,about_id,alias,connection,provider,model,data_class,input_tokens,output_tokens,cost,duration_ms\n");
        foreach (AiUsageRecord r in records)
        {
            string? scope = r.ScopeId is { } id && names.TryGetValue(id, out AiScopeInfo? info) ? info.Key : null;
            csv.AppendJoin(',', [
                r.At.ToString("O", CultureInfo.InvariantCulture), r.ScopeId?.ToString(), Csv(scope), r.AccountId?.ToString(), r.OnBehalfOf?.ToString(), Csv(r.Purpose),
                Csv(r.AboutKind), Csv(r.AboutId), Csv(r.Alias), Csv(r.Connection), Csv(r.Provider), Csv(r.Model), Csv(r.DataClass),
                r.InputTokens.ToString(CultureInfo.InvariantCulture), r.OutputTokens.ToString(CultureInfo.InvariantCulture), r.Cost.ToString(CultureInfo.InvariantCulture),
                r.DurationMs?.ToString(CultureInfo.InvariantCulture)]).Append('\n');
        }

        return csv.ToString();
    }

    private async Task MayReadAsync(AiActor actor, Guid? scopeId, CancellationToken ct)
    {
        if (!await admin.AdministersAsync(actor, scopeId, ct))
        {
            throw new UserFriendlyException(scopeId is null ? "Only the site's admins read the whole site's AI usage." : "Only this scope's admins read its AI usage.",
                AiErrorCodes.Forbidden);
        }
    }

    private async Task<AiUsageReport> ReportAsync(AiUsageQuery query, System.Linq.Expressions.Expression<Func<AiUsageRecord, bool>> whose, CancellationToken ct)
    {
        if (!AiUsageGroups.All.Contains(query.GroupBy))
        {
            throw new UserFriendlyException($"Usage groups by {string.Join(", ", AiUsageGroups.All)}.", AiErrorCodes.Invalid);
        }

        if (query.To <= query.From || query.To - query.From > TimeSpan.FromDays(400))
        {
            throw new UserFriendlyException("Usage is read over a period of up to 400 days.", AiErrorCodes.Invalid);
        }

        List<Bucket> buckets = await aiDb.RunAsync(db =>
        {
            IQueryable<AiUsageRecord> done = Filter(db, query).Where(whose).Where(r => r.State == AiUsageStates.Done);
            return query.GroupBy switch
            {
                AiUsageGroups.Scope => SumAsync(done.GroupBy(r => r.ScopeId), k => k?.ToString(), ct),
                AiUsageGroups.Person => SumAsync(done.GroupBy(r => r.OnBehalfOf ?? r.AccountId), k => k?.ToString(), ct),
                AiUsageGroups.Account => SumAsync(done.GroupBy(r => r.AccountId), k => k?.ToString(), ct),
                AiUsageGroups.Purpose => SumAsync(done.GroupBy(r => r.Purpose), k => k, ct),
                AiUsageGroups.Model => SumAsync(done.GroupBy(r => r.Model), k => k, ct),
                AiUsageGroups.Connection => SumAsync(done.GroupBy(r => r.Connection), k => k, ct),
                AiUsageGroups.DataClass => SumAsync(done.GroupBy(r => r.DataClass), k => k, ct),
                _ => SumAsync(done.GroupBy(r => r.At.Date), k => k.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct),
            };
        }, cancellationToken: ct);

        List<AiUsageRow> rows = query.GroupBy == AiUsageGroups.Scope ? await UnderAsync(query.ScopeId, buckets, ct)
            : [.. buckets.Select(b => new AiUsageRow(b.Key ?? "", null, b.Calls, b.Input, b.Output, b.Cost))];
        rows = [.. rows.OrderByDescending(r => r.Cost).ThenBy(r => r.Key, StringComparer.Ordinal)];
        AiUsageRow total = new("total", null, rows.Sum(r => r.Calls), rows.Sum(r => r.InputTokens), rows.Sum(r => r.OutputTokens), rows.Sum(r => r.Cost));
        return new AiUsageReport(query, rows, total);
    }

    /// <summary>Rolls each call's scope up to the scope right under the one asked about (its own calls under its own id).</summary>
    private async Task<List<AiUsageRow>> UnderAsync(Guid? top, List<Bucket> buckets, CancellationToken ct)
    {
        Dictionary<string, (int Calls, long Input, long Output, decimal Cost)> rolled = [];
        foreach (Bucket b in buckets)
        {
            string key = "";
            if (b.Key is not null && Guid.TryParse(b.Key, out Guid scope))
            {
                IReadOnlyList<Guid> down = [.. (await chains.OfAsync(scope, ct)).Up.Reverse()];
                int at = top is null ? -1 : down.ToList().IndexOf(top.Value);
                key = (at + 1 < down.Count ? down[at + 1] : down[^1]).ToString();
            }

            rolled[key] = rolled.TryGetValue(key, out var r) ? (r.Calls + b.Calls, r.Input + b.Input, r.Output + b.Output, r.Cost + b.Cost) : (b.Calls, b.Input, b.Output, b.Cost);
        }

        IReadOnlyDictionary<Guid, AiScopeInfo> names = await scopes.GetManyAsync(rolled.Keys.Where(k => k.Length > 0).Select(Guid.Parse), ct);
        return [.. rolled.Select(r => new AiUsageRow(r.Key, r.Key.Length == 0 ? "(the site)" : names.TryGetValue(Guid.Parse(r.Key), out AiScopeInfo? n) ? n.Title : null,
            r.Value.Calls, r.Value.Input, r.Value.Output, r.Value.Cost))];
    }

    private static async Task<List<Bucket>> SumAsync<TKey>(IQueryable<IGrouping<TKey, AiUsageRecord>> groups, Func<TKey, string?> key, CancellationToken ct)
    {
        var sums = await groups.Select(g => new { g.Key, Calls = g.Count(), Input = g.Sum(r => (long)r.InputTokens), Output = g.Sum(r => (long)r.OutputTokens),
            Cost = g.Sum(r => r.Cost) }).ToListAsync(ct);
        return [.. sums.Select(s => new Bucket(key(s.Key), s.Calls, s.Input, s.Output, s.Cost))];
    }

    private static IQueryable<AiUsageRecord> Filter(IAiDbContext db, AiUsageQuery query)
    {
        DateTime from = DateTime.SpecifyKind(query.From, DateTimeKind.Utc), to = DateTime.SpecifyKind(query.To, DateTimeKind.Utc);
        IQueryable<AiUsageRecord> records = db.AiUsage.AsNoTracking().Where(r => r.At >= from && r.At < to);
        if (query.ScopeId is { } scope)
        {
            records = records.Where(r => r.ScopePath.Contains(scope));
        }

        if (query.AccountId is { } account)
        {
            records = records.Where(r => r.AccountId == account || r.OnBehalfOf == account);
        }

        if (query.Purpose is { Length: > 0 } purpose)
        {
            records = purpose.EndsWith('*') ? records.Where(r => r.Purpose.StartsWith(purpose.Substring(0, purpose.Length - 1))) : records.Where(r => r.Purpose == purpose);
        }

        if (query.Model is { Length: > 0 } model)
        {
            records = records.Where(r => r.Model == model);
        }

        return records;
    }

    private static string? Csv(string? value) =>
        value is null ? null : value.IndexOfAny([',', '"', '\n', '\r']) >= 0 || value.StartsWith('=') || value.StartsWith('+') || value.StartsWith('-') || value.StartsWith('@')
            ? "\"" + (value.StartsWith('=') || value.StartsWith('+') || value.StartsWith('-') || value.StartsWith('@') ? "'" : "") + value.Replace("\"", "\"\"") + "\""
            : value;

    private sealed record Bucket(string? Key, int Calls, long Input, long Output, decimal Cost);
}
