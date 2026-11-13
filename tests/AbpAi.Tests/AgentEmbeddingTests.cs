using AbpAgent;
using AbpAi.Agents;

namespace AbpAi.Tests;

/// <summary>
/// An agent's memory embedded through the bridge (AbpAgent's <c>IAgentEmbeddings</c>, AbpAgent.Graphiti's): the agent's
/// alias, account and scope, its purpose and what the texts are about reach the gateway, the vectors come back as it
/// made them, and a refusal is AbpAgent's with the gateway's code.
/// </summary>
public class AgentEmbeddingTests
{
    [Fact]
    public async Task An_agents_memory_is_embedded_through_the_gateway_as_the_agent()
    {
        EmbeddingAi ai = new();
        Guid agent = Guid.NewGuid(), scope = Guid.NewGuid(), person = Guid.NewGuid();
        AiAgentEmbeddings embeddings = new(ai, new ClassAndPerson(person, AiDataClasses.Internal));

        AgentEmbedding made = await embeddings.EmbedAsync(new AgentEmbedRequest(agent, scope, "embed", ["Sara owns the March invoice"], "agent:ask:memory:embed")
        {
            Dimensions = 1024, InputType = AiEmbedInputTypes.Document, AboutKind = "episode", AboutId = "e9",
        }, CancellationToken.None);

        AiEmbedRequest sent = ai.Sent!;
        Assert.Equal(("embed", agent, scope, person, AiDataClasses.Internal, "agent:ask:memory:embed"),
            (sent.Alias, sent.AccountId!.Value, sent.ScopeId!.Value, sent.OnBehalfOf!.Value, sent.DataClass, sent.Purpose));
        Assert.Equal((1024, AiEmbedInputTypes.Document, new AiSubject("episode", "e9")), (sent.Dimensions!.Value, sent.InputType, sent.About));
        Assert.Equal((1, 1024, "vector-small", 9, 0.002m), (made.Vectors.Count, made.Vectors[0].Length, made.Model, made.InputTokens, made.Cost));

        ai.Refuse = new AiRefusedException(AiRefusalCodes.DataNotAllowed, "\"embed\" leads only to models that may not receive internal data.");
        AgentModelRefusedException refused = await Assert.ThrowsAsync<AgentModelRefusedException>(() =>
            embeddings.EmbedAsync(new AgentEmbedRequest(agent, scope, "embed", ["x"], "agent:ask:memory:embed"), CancellationToken.None));
        Assert.Equal(AiRefusalCodes.DataNotAllowed, refused.Code);
    }

    private sealed class ClassAndPerson(Guid person, string dataClass) : IAiAgentCalls
    {
        public Task<AiAgentCallInfo> DescribeAsync(AgentModelRequest request, CancellationToken cancellationToken) => Task.FromResult(new AiAgentCallInfo(null, null));

        public Task<AiAgentCallInfo> DescribeEmbedAsync(AgentEmbedRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new AiAgentCallInfo(person, dataClass));
    }

    private sealed class EmbeddingAi : IAi
    {
        public AiEmbedRequest? Sent { get; private set; }

        public AiRefusedException? Refuse { get; set; }

        public Task<AiEmbedding> EmbedAsync(AiEmbedRequest request, CancellationToken cancellationToken = default)
        {
            Sent = request;
            return Refuse is not null
                ? throw Refuse
                : Task.FromResult(new AiEmbedding([.. request.Inputs.Select(_ => new float[request.Dimensions ?? 8])], "vector-small", new AiUsage(9, 0, 0.002m)));
        }

        public Task<AiReply> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<AiStreamEvent> StreamAsync(AiRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AiChoice> ResolveAsync(string alias, Guid? scopeId = null, string? dataClass = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
