using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AbpAgent;
using Volo.Abp.Modularity;

namespace AbpAi.Agents;

/// <summary>What an agent's call carries beyond AbpAgent's request: the run's person and its data class, and how its turns are made.</summary>
public sealed record AiAgentCallInfo(Guid? OnBehalfOf, string? DataClass)
{
    /// <summary>
    /// Let the provider cache each turn's start (<see cref="AiRequest.Cache"/>): a run re-sends its conversation every
    /// turn, so this is on unless the host turns it off.
    /// </summary>
    public bool Cache { get; init; } = true;

    /// <summary>The fewest tokens a turn may answer with: a floor on AbpAgent's per-turn <c>MaxTokens</c> (thinking takes room). Per-call limits still cap it.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>More of the provider's request (<see cref="AiRequest.ProviderOptions"/>): thinking, effort, a tool choice.</summary>
    public JsonElement? ProviderOptions { get; init; }
}

/// <summary>
/// Tells the bridge, for an agent's call, the person the run is for (so per-person limits apply to them too) and the
/// class of data the conversation carries (the item's, a model set's policy). The default says neither: the call
/// counts against the agent's account alone and as the site's highest data class, so it goes only where everything may.
/// </summary>
public interface IAiAgentCalls
{
    Task<AiAgentCallInfo> DescribeAsync(AgentModelRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The same for an agent's memory being embedded (<see cref="AgentEmbedRequest"/>): the person it's for and the class
    /// of data its texts carry. The default says neither, so the texts count as the site's highest class.
    /// </summary>
    Task<AiAgentCallInfo> DescribeEmbedAsync(AgentEmbedRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new AiAgentCallInfo(null, null));
}

public sealed class NoAiAgentCallInfo : IAiAgentCalls
{
    public Task<AiAgentCallInfo> DescribeAsync(AgentModelRequest request, CancellationToken cancellationToken) => Task.FromResult(new AiAgentCallInfo(null, null));
}

/// <summary>
/// AbpAgent's model on AbpAi: a run's call names the agent's alias, the agent's account and the run's scope, so the
/// gateway's aliases, the per-agent and per-person limits and the data rules all apply, and its usage is recorded as
/// <c>agent:{key}</c> about the run's item. Each turn may be cached by the provider (<see cref="AiAgentCallInfo.Cache"/>). A
/// refusal ends the run with the gateway's code, and so does a model that declines to answer (its <c>stop_reason</c> is
/// <c>refusal</c>), with the code <c>refusal</c>. A live chat's turn streams (<see cref="StreamAsync"/>): the Messages API's
/// events as the model writes them, then the whole reply; a refusal before the first event is thrown, and a failure after
/// it is the Messages API's <c>error</c> event, its type the gateway's code.
/// </summary>
public sealed class AiAgentModels(IAi ai, IAiAgentCalls calls) : IAgentModels
{
    /// <summary>The stop reason of a model that declined to answer, and the code a run ends with then.</summary>
    public const string RefusalStop = "refusal";

    public async Task<AgentModelReply> CompleteAsync(AgentModelRequest request, CancellationToken cancellationToken)
    {
        AiRequest call = await CallAsync(request, cancellationToken);
        try
        {
            AiReply reply = await ai.CompleteAsync(call, cancellationToken);
            if (reply.StopReason == RefusalStop)
            {
                throw new AgentModelRefusedException(RefusalStop, Declined(reply));
            }

            return Reply(reply);
        }
        catch (AiRefusedException ex)
        {
            throw new AgentModelRefusedException(ex.Code, ex.Message);
        }
    }

    public async IAsyncEnumerable<AgentModelEvent> StreamAsync(AgentModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AiRequest call = await CallAsync(request, cancellationToken);
        IAsyncEnumerator<AiStreamEvent> events = ai.StreamAsync(call, cancellationToken).GetAsyncEnumerator(cancellationToken);
        bool started = false;
        try
        {
            while (true)
            {
                AiStreamEvent? next = null;
                AiRefusedException? failed = null;
                try
                {
                    if (!await events.MoveNextAsync())
                    {
                        yield break;
                    }

                    next = events.Current;
                }
                catch (AiRefusedException ex) when (!started)
                {
                    throw new AgentModelRefusedException(ex.Code, ex.Message);
                }
                catch (AiRefusedException ex)
                {
                    failed = ex;
                }

                if (failed is not null)
                {
                    // It began: the run keeps what was written so far, and ends with the gateway's code as the error's type.
                    yield return Error(failed.Code, failed.Message);
                    yield break;
                }

                if (next!.Type == AiStreamEvent.Done && next.Reply is { } reply)
                {
                    yield return reply.StopReason == RefusalStop ? Error(RefusalStop, Declined(reply)) : AgentModelEvent.Finished(Reply(reply));
                    yield break;
                }

                started = true;
                yield return new AgentModelEvent(next.Type, next.Data);
            }
        }
        finally
        {
            await events.DisposeAsync();
        }
    }

    /// <summary>The Messages API's <c>error</c> event, its type the gateway's code.</summary>
    private static AgentModelEvent Error(string code, string message) =>
        new("error", JsonSerializer.SerializeToElement(new { type = "error", error = new { type = code, message } }));

    private static string Declined(AiReply reply) => string.IsNullOrWhiteSpace(reply.Text)
        ? $"The model ({reply.Model}) declined to answer."
        : $"The model ({reply.Model}) declined to answer: {(reply.Text.Length > 500 ? reply.Text[..500] : reply.Text)}";

    /// <summary>
    /// The reply as AbpAgent takes it, with every token the turn sent (the ones the provider wrote to its cache or read from
    /// it too), so a run's token total and AbpAgent's token limits count what the run sent; the cost is already right.
    /// </summary>
    private static AgentModelReply Reply(AiReply reply) =>
        new(reply.Message, reply.Model, reply.Usage.InputTokens + reply.Usage.CacheWriteTokens + reply.Usage.CacheReadTokens, reply.Usage.OutputTokens,
            reply.Usage.Cost);

    private async Task<AiRequest> CallAsync(AgentModelRequest request, CancellationToken cancellationToken)
    {
        AiAgentCallInfo info = await calls.DescribeAsync(request, cancellationToken);
        return new AiRequest(request.Alias, request.Messages)
        {
            System = request.System,
            Tools = request.Tools,
            MaxTokens = info.MaxTokens is { } floor && floor > request.MaxTokens ? floor : request.MaxTokens,
            Cache = info.Cache,
            ProviderOptions = info.ProviderOptions,
            Purpose = request.Purpose,
            ScopeId = request.ScopeId,
            AccountId = request.AccountId,
            OnBehalfOf = info.OnBehalfOf,
            DataClass = info.DataClass,
            About = request.ItemId is { } item ? new AiSubject("item", item.ToString()) : null,
        };
    }
}

/// <summary>
/// AbpAgent's embeddings on AbpAi (an agent's graph memory, AbpAgent.Graphiti): the agent's alias, account and scope, its
/// purpose and what the texts are about, so the gateway's aliases, limits, data rules and ledger hold as for its turns.
/// The vectors come back in order and of the size asked for; a refusal is AbpAgent's, with the gateway's code.
/// </summary>
public sealed class AiAgentEmbeddings(IAi ai, IAiAgentCalls calls) : IAgentEmbeddings
{
    public async Task<AgentEmbedding> EmbedAsync(AgentEmbedRequest request, CancellationToken cancellationToken)
    {
        AiAgentCallInfo info = await calls.DescribeEmbedAsync(request, cancellationToken);
        try
        {
            AiEmbedding made = await ai.EmbedAsync(new AiEmbedRequest(request.Alias, request.Texts)
            {
                AccountId = request.AccountId,
                ScopeId = request.ScopeId,
                OnBehalfOf = info.OnBehalfOf,
                DataClass = info.DataClass,
                Purpose = request.Purpose,
                About = request.AboutKind is { Length: > 0 } kind && request.AboutId is { Length: > 0 } id ? new AiSubject(kind, id) : null,
                Dimensions = request.Dimensions,
                InputType = request.InputType,
            }, cancellationToken);
            return new AgentEmbedding(made.Vectors, made.Model, made.Usage.InputTokens, made.Usage.Cost);
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
        context.Services.Replace(ServiceDescriptor.Transient<IAgentEmbeddings, AiAgentEmbeddings>());
    }
}
