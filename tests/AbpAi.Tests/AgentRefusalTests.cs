using System.Runtime.CompilerServices;
using AbpAgent;
using AbpAi.Agents;

namespace AbpAi.Tests;

/// <summary>
/// A host words the gateway's refusals its own way (<see cref="IAiAgentCalls.Refused"/>): a turn's, a live chat's before
/// and after its first event, and an embedding's all reach AbpAgent in the host's code and words. Unsaid, they're the
/// gateway's own.
/// </summary>
public class AgentRefusalTests
{
    private static readonly AgentModelRequest Turn = new(Guid.NewGuid(), Guid.NewGuid(), "smart", AiMessages.User("hi"), "You are Ask.", null, 200, "agent:ask", null);

    private static readonly AgentEmbedRequest Embed = new(Guid.NewGuid(), Guid.NewGuid(), "embed", ["x"], "agent:ask:memory:embed");

    private static readonly AiRefusedException OverLimit = new(AiRefusalCodes.OverLimit, "This account's daily AI call limit on the site is reached (1 of 1 calls).");

    [Fact]
    public async Task By_default_a_refusal_is_the_gateways_own()
    {
        AgentModelRefusedException refused = await Assert.ThrowsAsync<AgentModelRefusedException>(() =>
            new AiAgentModels(new RefusingAi(OverLimit), new NoAiAgentCallInfo()).CompleteAsync(Turn, CancellationToken.None));
        Assert.Equal((AiRefusalCodes.OverLimit, OverLimit.Message), (refused.Code, refused.Message));
    }

    [Fact]
    public async Task A_hosts_words_reach_a_turn_a_live_chat_and_an_embedding()
    {
        HostWords host = new();

        AgentModelRefusedException turn = await Assert.ThrowsAsync<AgentModelRefusedException>(() =>
            new AiAgentModels(new RefusingAi(OverLimit), host).CompleteAsync(Turn, CancellationToken.None));
        Assert.Equal(("limit_reached", "turn: The AI limit is reached."), (turn.Code, turn.Message));

        AgentModelRefusedException beforeFirst = await Assert.ThrowsAsync<AgentModelRefusedException>(async () =>
        {
            await foreach (AgentModelEvent _ in new AiAgentModels(new RefusingAi(OverLimit), host).StreamAsync(Turn, CancellationToken.None))
            {
            }
        });
        Assert.Equal("limit_reached", beforeFirst.Code);

        List<AgentModelEvent> events = [];
        await foreach (AgentModelEvent e in new AiAgentModels(new RefusingAi(OverLimit) { After = 1 }, host).StreamAsync(Turn, CancellationToken.None))
        {
            events.Add(e);
        }

        Assert.Equal(["message_start", "error"], events.Select(e => e.Event));
        Assert.Equal(("limit_reached", "turn: The AI limit is reached."),
            (events[^1].Data.GetProperty("error").GetProperty("type").GetString(), events[^1].Data.GetProperty("error").GetProperty("message").GetString()));

        AgentModelRefusedException embed = await Assert.ThrowsAsync<AgentModelRefusedException>(() =>
            new AiAgentEmbeddings(new RefusingAi(OverLimit), host).EmbedAsync(Embed, CancellationToken.None));
        Assert.Equal(("limit_reached", "embedding: The AI limit is reached."), (embed.Code, embed.Message));
    }

    /// <summary>A host with its own words for an over-limit refusal, saying whether a turn or an embedding met it.</summary>
    private sealed class HostWords : IAiAgentCalls
    {
        public Task<AiAgentCallInfo> DescribeAsync(AgentModelRequest request, CancellationToken cancellationToken) => Task.FromResult(new AiAgentCallInfo(null, null));

        public AgentModelRefusedException Refused(AiRefusedException refused, AgentModelRequest? turn, AgentEmbedRequest? embed) =>
            refused.Code == AiRefusalCodes.OverLimit
                ? new("limit_reached", $"{(turn is not null ? "turn" : "embedding")}: The AI limit is reached.")
                : new(refused.Code, refused.Message);
    }

    /// <summary>A gateway that refuses: at once, or a live chat's after <see cref="After"/> events.</summary>
    private sealed class RefusingAi(AiRefusedException refusal) : IAi
    {
        public int After { get; init; }

        public Task<AiReply> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default) => throw refusal;

        public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (int i = 0; i < After; i++)
            {
                await Task.Yield();
                yield return new AiStreamEvent("message_start", default);
            }

            throw refusal;
        }

        public Task<AiEmbedding> EmbedAsync(AiEmbedRequest request, CancellationToken cancellationToken = default) => throw refusal;

        public Task<AiChoice> ResolveAsync(string alias, Guid? scopeId = null, string? dataClass = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
