using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AbpAgent;
using Volo.Abp.Modularity;

namespace AbpAi.Agents;

/// <summary>What an agent's call carries beyond AbpAgent's request: the run's person and its data class.</summary>
public sealed record AiAgentCallInfo(Guid? OnBehalfOf, string? DataClass);

/// <summary>
/// Tells the bridge, for an agent's call, the person the run is for (so per-person limits apply to them too) and the
/// class of data the conversation carries (the item's, a model set's policy). The default says neither: the call
/// counts against the agent's account alone and as the site's highest data class, so it goes only where everything may.
/// </summary>
public interface IAiAgentCalls
{
    Task<AiAgentCallInfo> DescribeAsync(AgentModelRequest request, CancellationToken cancellationToken);
}

public sealed class NoAiAgentCallInfo : IAiAgentCalls
{
    public Task<AiAgentCallInfo> DescribeAsync(AgentModelRequest request, CancellationToken cancellationToken) => Task.FromResult(new AiAgentCallInfo(null, null));
}

/// <summary>
/// AbpAgent's model on AbpAi: a run's call names the agent's alias, the agent's account and the run's scope, so the
/// gateway's aliases, the per-agent and per-person limits and the data rules all apply, and its usage is recorded as
/// <c>agent:{key}</c> about the run's item. A refusal ends the run with the gateway's code.
/// </summary>
public sealed class AiAgentModels(IAi ai, IAiAgentCalls calls) : IAgentModels
{
    public async Task<AgentModelReply> CompleteAsync(AgentModelRequest request, CancellationToken cancellationToken)
    {
        AiAgentCallInfo info = await calls.DescribeAsync(request, cancellationToken);
        AiRequest call = new(request.Alias, request.Messages)
        {
            System = request.System,
            Tools = request.Tools,
            MaxTokens = request.MaxTokens,
            Purpose = request.Purpose,
            ScopeId = request.ScopeId,
            AccountId = request.AccountId,
            OnBehalfOf = info.OnBehalfOf,
            DataClass = info.DataClass,
            About = request.ItemId is { } item ? new AiSubject("item", item.ToString()) : null,
        };
        try
        {
            AiReply reply = await ai.CompleteAsync(call, cancellationToken);
            return new AgentModelReply(reply.Message, reply.Model, reply.Usage.InputTokens, reply.Usage.OutputTokens, reply.Usage.Cost);
        }
        catch (AiRefusedException ex)
        {
            throw new AgentModelRefusedException(ex.Code, ex.Message);
        }
    }
}

/// <summary>Makes AbpAi the model behind AbpAgent: <c>[DependsOn(typeof(AbpAiAgentsModule))]</c> in the host's module.</summary>
[DependsOn(typeof(AbpAiModule))]
public class AbpAiAgentsModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.TryAddTransient<IAiAgentCalls, NoAiAgentCallInfo>();
        context.Services.Replace(ServiceDescriptor.Transient<IAgentModels, AiAgentModels>());
    }
}
