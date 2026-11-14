using Microsoft.AspNetCore.Mvc;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;

namespace AbpAi.SampleHost;

/// <summary>A call as the sample takes it: a prompt to an alias in a scope (by key), with the data class it carries.</summary>
public sealed record AskInput(string Alias, string Prompt, string? Scope = null, string? DataClass = null, string Purpose = "ask", Guid? OnBehalfOf = null);

public sealed record AskOutput(string Text, string Model, string Connection, AiUsage Usage);

/// <summary>
/// The sample's own call over the gateway; its admin API, usage and exports are AbpAi.HttpApi's, under the same
/// <c>api/ai</c>. Who's acting is the X-Account header (<see cref="XAccount"/>). A refusal is 422 with its code and, for a
/// limit, which and when it frees.
/// </summary>
[Route("api/ai")]
public class AiController(IAi ai, SampleOrg org) : AbpControllerBase
{
    private AiActor Actor => Request.Headers.TryGetValue("X-Account", out var value) && Guid.TryParse(value, out Guid id)
        ? new AiActor(id)
        : throw new UserFriendlyException("Say who you are in the X-Account header.", AiErrorCodes.Forbidden);

    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] AskInput input, CancellationToken ct)
    {
        try
        {
            AiReply reply = await ai.CompleteAsync(new AiRequest(input.Alias, AiMessages.User(input.Prompt))
            {
                ScopeId = string.IsNullOrEmpty(input.Scope) ? null : org.IdOf(input.Scope), AccountId = Actor.Id, OnBehalfOf = input.OnBehalfOf,
                DataClass = input.DataClass, Purpose = input.Purpose,
            }, ct);
            return Ok(new AskOutput(reply.Text, reply.Model, reply.Connection, reply.Usage));
        }
        catch (AiRefusedException ex)
        {
            return UnprocessableEntity(new { code = ex.Code, message = ex.Message, limit = ex.Limit });
        }
    }
}
