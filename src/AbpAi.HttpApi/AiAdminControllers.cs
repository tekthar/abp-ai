using Microsoft.AspNetCore.Mvc;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;

namespace AbpAi.HttpApi;

/// <summary>The site's AI settings: whether scopes may connect, prompts kept and for how long, the data classes.</summary>
[Route("site")]
public class AiSiteController(AiAdminService admin, IAiCurrentActor actor) : AbpControllerBase
{
    [HttpGet]
    public async Task<AiSiteSettings> Get(CancellationToken ct) => await admin.GetSiteAsync(await actor.GetAsync(ct), ct);

    [HttpPut, AiSensitive]
    public async Task<AiSiteSettings> Set([FromBody] AiSiteSettings input, CancellationToken ct) => await admin.SetSiteAsync(await actor.GetAsync(ct), input, ct);
}

/// <summary>
/// Connections to model providers, the site's or a scope's (<c>scopeId</c>). A host that keeps its own connections lists
/// them here and sets them where it keeps them: creating, changing or deleting one answers 409.
/// </summary>
[Route("connections")]
public class AiConnectionsController(AiAdminService admin, IAiCurrentActor actor) : AbpControllerBase
{
    [HttpGet]
    public async Task<List<AiConnectionInfo>> List([FromQuery] Guid? scopeId, CancellationToken ct) =>
        await admin.ListConnectionsAsync(await actor.GetAsync(ct), scopeId, ct);

    [HttpPost, AiSensitive]
    public async Task<AiConnectionInfo> Create([FromQuery] Guid? scopeId, [FromBody] AiConnectionInput input, CancellationToken ct) =>
        await admin.CreateConnectionAsync(await actor.GetAsync(ct), scopeId, input, ct);

    [HttpPut("{id:guid}"), AiSensitive]
    public async Task<AiConnectionInfo> Update(Guid id, [FromBody] AiConnectionInput input, CancellationToken ct) =>
        await admin.UpdateConnectionAsync(await actor.GetAsync(ct), id, input, ct);

    [HttpDelete("{id:guid}"), AiSensitive]
    public async Task Delete(Guid id, CancellationToken ct) => await admin.DeleteConnectionAsync(await actor.GetAsync(ct), id, ct);

    /// <summary>One short call through the connection (a model of its own, or the one named), and what came back.</summary>
    [HttpPost("{id:guid}/test")]
    public async Task<AiConnectionTest> Test(Guid id, [FromQuery] Guid? scopeId, [FromQuery] string? model, CancellationToken ct) =>
        await admin.TestConnectionAsync(await actor.GetAsync(ct), scopeId, id, model, ct);

    /// <summary>The models the provider offers through the connection, as it lists them now.</summary>
    [HttpGet("{id:guid}/provider-models")]
    public async Task<IReadOnlyList<string>> ProviderModels(Guid id, [FromQuery] Guid? scopeId, CancellationToken ct) =>
        await admin.ProviderModelsAsync(await actor.GetAsync(ct), scopeId, id, ct);

    /// <summary>Asks the provider for its models and keeps them on the connection.</summary>
    [HttpPost("{id:guid}/discover"), AiSensitive]
    public async Task<AiConnectionInfo> Discover(Guid id, CancellationToken ct) => await admin.DiscoverModelsAsync(await actor.GetAsync(ct), id, ct);
}

/// <summary>Aliases (<c>smart</c>, <c>embed</c>): the models a name leads to, in order, the site's or a scope's.</summary>
[Route("aliases")]
public class AiAliasesController(AiAdminService admin, IAiCurrentActor actor) : AbpControllerBase
{
    [HttpGet]
    public async Task<List<AiAliasInfo>> List([FromQuery] Guid? scopeId, CancellationToken ct) => await admin.ListAliasesAsync(await actor.GetAsync(ct), scopeId, ct);

    [HttpPut("{name}"), AiSensitive]
    public async Task<AiAliasInfo> Set(string name, [FromQuery] Guid? scopeId, [FromBody] List<AiAliasTarget> targets, CancellationToken ct) =>
        await admin.SetAliasAsync(await actor.GetAsync(ct), scopeId, name, targets, ct);

    [HttpDelete("{name}"), AiSensitive]
    public async Task Delete(string name, [FromQuery] Guid? scopeId, CancellationToken ct) =>
        await admin.DeleteAliasAsync(await actor.GetAsync(ct), scopeId, name, ct);
}

/// <summary>Limits on calls, tokens or cost, per period: the site's or a scope's, per person or per group.</summary>
[Route("limits")]
public class AiLimitsController(AiAdminService admin, IAiCurrentActor actor) : AbpControllerBase
{
    [HttpGet]
    public async Task<List<AiLimitInfo>> List([FromQuery] Guid? scopeId, CancellationToken ct) => await admin.ListLimitsAsync(await actor.GetAsync(ct), scopeId, ct);

    /// <summary>Sets a limit: the one of its kind, period and person or group is replaced.</summary>
    [HttpPost, AiSensitive]
    public async Task<AiLimitInfo> Set([FromQuery] Guid? scopeId, [FromBody] AiLimitInput input, CancellationToken ct) =>
        await admin.SetLimitAsync(await actor.GetAsync(ct), scopeId, input, ct);

    [HttpDelete("{id:guid}"), AiSensitive]
    public async Task Delete(Guid id, CancellationToken ct) => await admin.DeleteLimitAsync(await actor.GetAsync(ct), id, ct);
}

/// <summary>An account's own limits on the site (an agent's), which count its calls everywhere.</summary>
[Route("accounts/{accountId:guid}/limits")]
public class AiAccountLimitsController(AiAdminService admin, IAiCurrentActor actor) : AbpControllerBase
{
    [HttpGet]
    public async Task<List<AiLimitInfo>> List(Guid accountId, CancellationToken ct) => await admin.ListAccountLimitsAsync(await actor.GetAsync(ct), accountId, ct);

    [HttpPost, AiSensitive]
    public async Task<AiLimitInfo> Set(Guid accountId, [FromBody] AiLimitInput input, CancellationToken ct) =>
        await admin.SetAccountLimitAsync(await actor.GetAsync(ct), accountId, input, ct);
}

/// <summary>Which of the site's models a scope may use, and whether it may add connections of its own.</summary>
[Route("scopes")]
public class AiScopesController(AiAdminService admin, IAiCurrentActor actor) : AbpControllerBase
{
    [HttpGet("access")]
    public async Task<List<AiScopeAccessInfo>> List(CancellationToken ct) => await admin.ListScopeAccessAsync(await actor.GetAsync(ct), ct);

    [HttpGet("{scopeId:guid}/access")]
    public async Task<AiScopeAccessInfo> Get(Guid scopeId, CancellationToken ct) => await admin.GetScopeAccessAsync(await actor.GetAsync(ct), scopeId, ct);

    [HttpPut("{scopeId:guid}/access"), AiSensitive]
    public async Task<AiScopeAccessInfo> Set(Guid scopeId, [FromBody] AiScopeAccessInput input, CancellationToken ct) =>
        await admin.SetScopeAccessAsync(await actor.GetAsync(ct), scopeId, input, ct);

    /// <summary>Back to the site's default for the scope.</summary>
    [HttpDelete("{scopeId:guid}/access"), AiSensitive]
    public async Task Delete(Guid scopeId, CancellationToken ct) => await admin.DeleteScopeAccessAsync(await actor.GetAsync(ct), scopeId, ct);
}

/// <summary>
/// Which model an alias leads to now, in a scope and for a class of data, for its administrators. A refusal (no model, or
/// none that may take the data) answers 422 with the gateway's code.
/// </summary>
[Route("resolve")]
public class AiResolveController(IAi ai, AiAdminService admin, IAiCurrentActor actor) : AbpControllerBase
{
    [HttpGet("{alias}")]
    public async Task<IActionResult> Resolve(string alias, [FromQuery] Guid? scopeId, [FromQuery] string? dataClass, CancellationToken ct)
    {
        if (!await admin.AdministersAsync(await actor.GetAsync(ct), scopeId, ct))
        {
            throw new UserFriendlyException("Only the site's or the scope's AI admins see where an alias leads.", AiErrorCodes.Forbidden);
        }

        try
        {
            return Ok(await ai.ResolveAsync(alias, scopeId, dataClass, ct));
        }
        catch (AiRefusedException ex)
        {
            return UnprocessableEntity(new { code = ex.Code, message = ex.Message });
        }
    }
}
