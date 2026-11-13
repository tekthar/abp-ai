using System.Runtime.CompilerServices;
using AbpAgent;
using AbpAi.Agents;

namespace AbpAi.Tests;

/// <summary>
/// A host maps an agent's scope to the gateway's (<see cref="AiAgentCallInfo.Scope"/>, <see cref="AiAgentCallInfo.About"/>):
/// unset, a call counts in the run's scope and is about the run's item; mapped to another scope, it counts there; moved to
/// the site, a turn is no longer about the item, while an embedding keeps its subject; and a host may name the subject itself.
/// </summary>
public class AgentScopeTests
{
    private static readonly Guid Agent = Guid.NewGuid(), Schema = Guid.NewGuid(), Project = Guid.NewGuid(), Item = Guid.NewGuid();

    private static AgentModelRequest Turn => new(Agent, Schema, "smart", AiMessages.User("hi"), "You are Ask.", null, 200, "agent:ask", Item);

    private static AgentEmbedRequest Embed => new(Agent, Schema, "embed", ["Sara owns the March invoice"], "agent:ask:memory:embed")
    {
        AboutKind = "episode", AboutId = "e9",
    };

    [Fact]
    public async Task Unset_a_call_counts_in_the_runs_scope_about_its_item()
    {
        RecordingAi ai = new();
        await new AiAgentModels(ai, new Mapping(null, null)).CompleteAsync(Turn, CancellationToken.None);
        await new AiAgentEmbeddings(ai, new Mapping(null, null)).EmbedAsync(Embed, CancellationToken.None);

        Assert.Equal(((Guid?)Schema, new AiSubject("item", Item.ToString())), (ai.Turn!.ScopeId, ai.Turn.About));
        Assert.Equal(((Guid?)Schema, new AiSubject("episode", "e9")), (ai.Embedding!.ScopeId, ai.Embedding.About));
    }

    [Fact]
    public async Task Mapped_to_another_scope_the_call_counts_there_with_the_hosts_subject()
    {
        RecordingAi ai = new();
        Mapping toProject = new(new AiAgentScope(Project), new AiSubject("record", "PROJ-7"));
        await new AiAgentModels(ai, toProject).CompleteAsync(Turn, CancellationToken.None);
        await new AiAgentEmbeddings(ai, toProject).EmbedAsync(Embed, CancellationToken.None);

        Assert.Equal(((Guid?)Project, new AiSubject("record", "PROJ-7")), (ai.Turn!.ScopeId, ai.Turn.About));
        Assert.Equal(((Guid?)Project, new AiSubject("record", "PROJ-7")), (ai.Embedding!.ScopeId, ai.Embedding.About));
        Assert.Equal((Agent, "agent:ask"), (ai.Turn.AccountId!.Value, ai.Turn.Purpose));
    }

    [Fact]
    public async Task Moved_to_the_site_a_turn_is_not_about_the_item_and_an_embedding_keeps_its_subject()
    {
        RecordingAi ai = new();
        Mapping toSite = new(AiAgentScope.Site, null);
        await new AiAgentModels(ai, toSite).CompleteAsync(Turn, CancellationToken.None);
        await new AiAgentEmbeddings(ai, toSite).EmbedAsync(Embed, CancellationToken.None);

        Assert.Equal(((Guid?)null, (AiSubject?)null), (ai.Turn!.ScopeId, ai.Turn.About));
        Assert.Equal(((Guid?)null, new AiSubject("episode", "e9")), (ai.Embedding!.ScopeId, ai.Embedding.About));

        // A live chat's turn is mapped the same way.
        await foreach (AgentModelEvent _ in new AiAgentModels(ai, toSite).StreamAsync(Turn, CancellationToken.None))
        {
        }

        Assert.Equal(((Guid?)null, (AiSubject?)null), (ai.Streamed!.ScopeId, ai.Streamed.About));
    }

    /// <summary>A host that maps every call to one scope and subject, for turns and embeddings alike.</summary>
    private sealed class Mapping(AiAgentScope? scope, AiSubject? about) : IAiAgentCalls
    {
        public Task<AiAgentCallInfo> DescribeAsync(AgentModelRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new AiAgentCallInfo(null, null) { Scope = scope, About = about });

        public Task<AiAgentCallInfo> DescribeEmbedAsync(AgentEmbedRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new AiAgentCallInfo(null, null) { Scope = scope, About = about });
    }

    /// <summary>A gateway that keeps the last turn, streamed turn and embedding it was asked for, and answers plainly.</summary>
    private sealed class RecordingAi : IAi
    {
        public AiRequest? Turn { get; private set; }

        public AiRequest? Streamed { get; private set; }

        public AiEmbedRequest? Embedding { get; private set; }

        public Task<AiReply> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            Turn = request;
            return Task.FromResult(Reply());
        }

        public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Streamed = request;
            await Task.Yield();
            yield return new AiStreamEvent(AiStreamEvent.Done, default) { Reply = Reply() };
        }

        public Task<AiEmbedding> EmbedAsync(AiEmbedRequest request, CancellationToken cancellationToken = default)
        {
            Embedding = request;
            return Task.FromResult(new AiEmbedding([.. request.Inputs.Select(_ => new float[8])], "vector-small", new AiUsage(3, 0, 0m)));
        }

        public Task<AiChoice> ResolveAsync(string alias, Guid? scopeId = null, string? dataClass = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static AiReply Reply() => new(AiMessages.AssistantText("hello"), "model-1", new AiUsage(4, 2, 0.01m));
    }
}
