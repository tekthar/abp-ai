using Microsoft.AspNetCore.Mvc;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;

namespace AbpAi.SampleHost;

/// <summary>A call as the sample takes it: a prompt to an alias in a scope (by key), with the data class it carries.</summary>
public sealed record AskInput(string Alias, string Prompt, string? Scope = null, string? DataClass = null, string Purpose = "ask", Guid? OnBehalfOf = null);

public sealed record AskOutput(string Text, string Model, string Connection, AiUsage Usage);

public sealed record AliasInput(IReadOnlyList<AiAliasTarget> Targets);

/// <summary>
/// The sample's API over the gateway: calls, the admin API, and usage. Who's acting is the X-Account header: the
/// sample has no sign-in, a real host has its own. A refusal is 422 with its code and, for a limit, which and when it frees.
/// </summary>
[Route("api/ai")]
public class AiController(IAi ai, AiAdminService admin, AiUsageService usage, AiExportService exports, SampleOrg org) : AbpControllerBase
{
    private AiActor Actor => Request.Headers.TryGetValue("X-Account", out var value) && Guid.TryParse(value, out Guid id)
        ? new AiActor(id)
        : throw new UserFriendlyException("Say who you are in the X-Account header.", AiErrorCodes.Forbidden);

    private Guid? ScopeOf(string? key) => string.IsNullOrEmpty(key) ? null : org.IdOf(key);

    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] AskInput input, CancellationToken ct)
    {
        try
        {
            AiReply reply = await ai.CompleteAsync(new AiRequest(input.Alias, AiMessages.User(input.Prompt))
            {
                ScopeId = ScopeOf(input.Scope), AccountId = Actor.Id, OnBehalfOf = input.OnBehalfOf, DataClass = input.DataClass, Purpose = input.Purpose,
            }, ct);
            return Ok(new AskOutput(reply.Text, reply.Model, reply.Connection, reply.Usage));
        }
        catch (AiRefusedException ex)
        {
            return UnprocessableEntity(new { code = ex.Code, message = ex.Message, limit = ex.Limit });
        }
    }

    [HttpGet("resolve/{alias}")]
    public async Task<IActionResult> Resolve(string alias, [FromQuery] string? scope, [FromQuery] string? dataClass, CancellationToken ct)
    {
        try
        {
            return Ok(await ai.ResolveAsync(alias, ScopeOf(scope), dataClass, ct));
        }
        catch (AiRefusedException ex)
        {
            return UnprocessableEntity(new { code = ex.Code, message = ex.Message });
        }
    }

    [HttpGet("site")]
    public Task<AiSiteSettings> GetSite(CancellationToken ct) => admin.GetSiteAsync(Actor, ct);

    [HttpPut("site")]
    public Task<AiSiteSettings> SetSite([FromBody] AiSiteSettings input, CancellationToken ct) => admin.SetSiteAsync(Actor, input, ct);

    [HttpGet("connections")]
    public Task<List<AiConnectionInfo>> Connections([FromQuery] string? scope, CancellationToken ct) => admin.ListConnectionsAsync(Actor, ScopeOf(scope), ct);

    [HttpPost("connections")]
    public Task<AiConnectionInfo> CreateConnection([FromQuery] string? scope, [FromBody] AiConnectionInput input, CancellationToken ct) =>
        admin.CreateConnectionAsync(Actor, ScopeOf(scope), input, ct);

    [HttpPut("connections/{id:guid}")]
    public Task<AiConnectionInfo> UpdateConnection(Guid id, [FromBody] AiConnectionInput input, CancellationToken ct) => admin.UpdateConnectionAsync(Actor, id, input, ct);

    [HttpPost("connections/{id:guid}/test")]
    public Task<AiConnectionTest> TestConnection(Guid id, [FromQuery] string? scope, [FromQuery] string? model, CancellationToken ct) =>
        admin.TestConnectionAsync(Actor, ScopeOf(scope), id, model, ct);

    [HttpGet("accounts/{id:guid}/limits")]
    public Task<List<AiLimitInfo>> AccountLimits(Guid id, CancellationToken ct) => admin.ListAccountLimitsAsync(Actor, id, ct);

    [HttpPut("accounts/{id:guid}/limits")]
    public Task<AiLimitInfo> SetAccountLimit(Guid id, [FromBody] AiLimitInput input, CancellationToken ct) => admin.SetAccountLimitAsync(Actor, id, input, ct);

    [HttpGet("aliases")]
    public Task<List<AiAliasInfo>> Aliases([FromQuery] string? scope, CancellationToken ct) => admin.ListAliasesAsync(Actor, ScopeOf(scope), ct);

    [HttpPut("aliases/{name}")]
    public Task<AiAliasInfo> SetAlias(string name, [FromQuery] string? scope, [FromBody] AliasInput input, CancellationToken ct) =>
        admin.SetAliasAsync(Actor, ScopeOf(scope), name, input.Targets, ct);

    [HttpGet("limits")]
    public Task<List<AiLimitInfo>> Limits([FromQuery] string? scope, CancellationToken ct) => admin.ListLimitsAsync(Actor, ScopeOf(scope), ct);

    [HttpPut("limits")]
    public Task<AiLimitInfo> SetLimit([FromQuery] string? scope, [FromBody] AiLimitInput input, CancellationToken ct) => admin.SetLimitAsync(Actor, ScopeOf(scope), input, ct);

    [HttpGet("usage")]
    public Task<AiUsageReport> Usage([FromQuery] string? scope, [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string groupBy = "scope",
        CancellationToken ct = default) => usage.QueryAsync(Actor, Query(scope, from, to, groupBy), ct);

    [HttpGet("usage/mine")]
    public Task<AiUsageReport> Mine([FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string groupBy = "purpose", CancellationToken ct = default)
    {
        AiUsageQuery q = Query(null, from, to, groupBy);
        return usage.MineAsync(Actor, q.From, q.To, groupBy, ct);
    }

    [HttpGet("usage/export")]
    public async Task<IActionResult> Export([FromQuery] string? scope, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
        File(System.Text.Encoding.UTF8.GetBytes(await usage.ExportCsvAsync(Actor, Query(scope, from, to, "scope"), ct)), "text/csv", "ai-usage.csv");

    [HttpGet("usage/calls")]
    public Task<IReadOnlyList<AiCallRow>> Calls([FromQuery] string? scope, [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? state,
        [FromQuery] int count = 50, CancellationToken ct = default) =>
        usage.RecentAsync(Actor, Query(scope, from, to, "scope") with { States = state is { Length: > 0 } ? [state] : null }, count, ct);

    [HttpPost("exports")]
    public Task<AiExportInfo> CreateExport([FromQuery] string? scope, [FromQuery] DateTime from, [FromQuery] DateTime to, CancellationToken ct) =>
        exports.CreateAsync(Actor, from, to, ScopeOf(scope), ct);

    [HttpGet("exports")]
    public Task<List<AiExportInfo>> Exports(CancellationToken ct) => exports.ListAsync(Actor, ct);

    [HttpGet("exports/{id:guid}")]
    public async Task<IActionResult> DownloadExport(Guid id, CancellationToken ct)
    {
        AiExportFile file = await exports.DownloadAsync(Actor, id, ct);
        return File(file.Content, file.ContentType, file.Name);
    }

    private AiUsageQuery Query(string? scope, DateTime? from, DateTime? to, string groupBy)
    {
        DateTime now = DateTime.UtcNow;
        DateTime start = from ?? new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return new AiUsageQuery(start, to ?? start.AddMonths(1), ScopeOf(scope), groupBy);
    }
}
