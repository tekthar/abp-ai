using System.Text;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp.AspNetCore.Mvc;

namespace AbpAi.HttpApi;

/// <summary>A period's calls for a recent-calls list: the query, how many (up to <see cref="AiUsageService.MaxRecent"/>), and which states (done, refused, failed).</summary>
public sealed record AiRecentQuery(DateTime From, DateTime To, Guid? ScopeId = null, int Count = 50, IReadOnlyList<string>? States = null);

/// <summary>
/// Usage from the ledger: totals for those who administer the site or the scope, grouped by scope, person, alias,
/// model, purpose or day; a person's own; recent calls; and a CSV of a period's calls.
/// </summary>
[Route("usage")]
public class AiUsageController(AiUsageService usage, IAiCurrentActor actor) : AbpControllerBase
{
    [HttpPost]
    public async Task<AiUsageReport> Query([FromBody] AiUsageQuery query, CancellationToken ct) => await usage.QueryAsync(await actor.GetAsync(ct), Utc(query), ct);

    /// <summary>The caller's own calls, wherever they made them.</summary>
    [HttpGet("mine")]
    public async Task<AiUsageReport> Mine([FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] string groupBy = AiUsageGroups.Day,
        CancellationToken ct = default) => await usage.MineAsync(await actor.GetAsync(ct), from.ToUniversalTime(), to.ToUniversalTime(), groupBy, ct);

    [HttpPost("recent")]
    public async Task<IReadOnlyList<AiCallRow>> Recent([FromBody] AiRecentQuery query, CancellationToken ct) =>
        await usage.RecentAsync(await actor.GetAsync(ct), new AiUsageQuery(query.From.ToUniversalTime(), query.To.ToUniversalTime(), query.ScopeId) { States = query.States }, query.Count, ct);

    /// <summary>The caller's own recent calls.</summary>
    [HttpGet("mine/recent")]
    public async Task<IReadOnlyList<AiCallRow>> MineRecent([FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] int count = 50,
        CancellationToken ct = default) => await usage.MineRecentAsync(await actor.GetAsync(ct), from.ToUniversalTime(), to.ToUniversalTime(), count, ct);

    /// <summary>A period's calls as CSV (up to <see cref="AiUsageService.MaxExportRows"/>), with no prompts or replies.</summary>
    [HttpPost("csv")]
    public async Task<IActionResult> Csv([FromBody] AiUsageQuery query, CancellationToken ct) =>
        File(Encoding.UTF8.GetBytes(await usage.ExportCsvAsync(await actor.GetAsync(ct), Utc(query), ct)), "text/csv", $"ai-usage-{query.From:yyyyMMdd}-{query.To:yyyyMMdd}.csv");

    private static AiUsageQuery Utc(AiUsageQuery query) => query with { From = query.From.ToUniversalTime(), To = query.To.ToUniversalTime() };
}

/// <summary>A period to export (up to 31 days), the whole site's or a scope's and those under it.</summary>
public sealed record AiExportRequest(DateTime From, DateTime To, Guid? ScopeId = null);

/// <summary>
/// Exports of a period's calls for support (one line of JSON each, gzipped, with prompts and replies where the site keeps
/// them), for the site's owner (<see cref="IAiRights.IsSiteOwnerAsync"/>). Kept a week; each is in the host's audit.
/// </summary>
[Route("exports")]
public class AiExportsController(AiExportService exports, IAiCurrentActor actor) : AbpControllerBase
{
    [HttpGet]
    public async Task<List<AiExportInfo>> List(CancellationToken ct) => await exports.ListAsync(await actor.GetAsync(ct), ct);

    [HttpPost, AiSensitive]
    public async Task<AiExportInfo> Create([FromBody] AiExportRequest input, CancellationToken ct) =>
        await exports.CreateAsync(await actor.GetAsync(ct), input.From.ToUniversalTime(), input.To.ToUniversalTime(), input.ScopeId, ct);

    /// <summary>The export's file. A POST, so a link can't fetch it and the host's sensitive check applies.</summary>
    [HttpPost("{id:guid}/file"), AiSensitive]
    public async Task<IActionResult> File(Guid id, CancellationToken ct)
    {
        AiExportFile file = await exports.DownloadAsync(await actor.GetAsync(ct), id, ct);
        return File(file.Content, file.ContentType, file.Name);
    }

    [HttpDelete("{id:guid}")]
    public async Task Delete(Guid id, CancellationToken ct) => await exports.DeleteAsync(await actor.GetAsync(ct), id, ct);
}
