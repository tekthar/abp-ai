using System.Runtime.CompilerServices;
using System.Text.Json;
using AbpAgent;
using AbpAi.Agents;

namespace AbpAi.Tests;

/// <summary>
/// A live chat's turn through the bridge (AbpAgent 0.3.0's <c>IAgentModels.StreamAsync</c>): the Messages API's events as
/// they come, then the whole reply; a refusal before the first event is thrown, a failure after it is the Messages API's
/// <c>error</c> event with the gateway's code as its type, and so is a model that declines.
/// </summary>
public class AgentStreamTests
{
    private static readonly AgentModelRequest Request = new(Guid.NewGuid(), Guid.NewGuid(), "smart", AiMessages.User("hi"), "You are Ask.", null, 4000, "agent:ask", null);

    [Fact]
    public async Task A_turn_streams_its_events_then_the_whole_reply_with_every_token_it_sent()
    {
        List<AgentModelEvent> events = await ReadAsync(new StreamingAi { Usage = new AiUsage(10, 5, 0.25m) { CacheWriteTokens = 2, CacheReadTokens = 30 } });

        Assert.Equal(["message_start", "content_block_delta", "message_stop", AgentModelEvent.Done], events.Select(e => e.Event));
        AgentModelReply reply = events[^1].Reply!;
        Assert.Equal(("model-1", 42, 5, 0.25m), (reply.Model, reply.InputTokens, reply.OutputTokens, reply.Cost));
    }

    [Fact]
    public async Task A_refusal_before_the_first_event_is_thrown_and_a_failure_after_it_ends_with_an_error_event()
    {
        AgentModelRefusedException refused = await Assert.ThrowsAsync<AgentModelRefusedException>(() =>
            ReadAsync(new StreamingAi { Fail = new AiRefusedException(AiRefusalCodes.OverLimit, "The site's daily AI limit is reached."), FailAfter = 0 }));
        Assert.Equal(AiRefusalCodes.OverLimit, refused.Code);

        List<AgentModelEvent> events = await ReadAsync(new StreamingAi
        {
            Fail = new AiRefusedException(AiRefusalCodes.Timeout, "Local sent nothing for 60 seconds (its idle timeout).") { TimedOut = AiTimeouts.Idle }, FailAfter = 2,
        });
        Assert.Equal(["message_start", "content_block_delta", "error"], events.Select(e => e.Event));
        Assert.Equal(AiRefusalCodes.Timeout, events[^1].Data.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_model_that_declines_ends_the_stream_with_a_refusal_error()
    {
        List<AgentModelEvent> events = await ReadAsync(new StreamingAi { StopReason = AiAgentModels.RefusalStop });
        Assert.Equal("error", events[^1].Event);
        Assert.Equal(AiAgentModels.RefusalStop, events[^1].Data.GetProperty("error").GetProperty("type").GetString());
    }

    private static async Task<List<AgentModelEvent>> ReadAsync(StreamingAi ai)
    {
        List<AgentModelEvent> events = [];
        await foreach (AgentModelEvent e in new AiAgentModels(ai, new NoAiAgentCallInfo()).StreamAsync(Request, CancellationToken.None))
        {
            events.Add(e);
        }

        return events;
    }

    /// <summary>A gateway that streams a short reply and its last event, or fails after so many events.</summary>
    private sealed class StreamingAi : IAi
    {
        public AiUsage Usage { get; init; } = new(1, 1, 0);

        public string StopReason { get; init; } = "end_turn";

        public AiRefusedException? Fail { get; init; }

        public int FailAfter { get; init; }

        public Task<AiReply> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            string[] types = ["message_start", "content_block_delta", "message_stop"];
            for (int i = 0; i < types.Length; i++)
            {
                if (Fail is not null && i == FailAfter)
                {
                    throw Fail;
                }

                await Task.Yield();
                yield return new AiStreamEvent(types[i], JsonSerializer.SerializeToElement(new { type = types[i] }));
            }

            JsonElement message = JsonSerializer.SerializeToElement(new { role = "assistant", content = new[] { new { type = "text", text = "Hello." } }, stop_reason = StopReason });
            yield return new AiStreamEvent(AiStreamEvent.Done, message) { Reply = new AiReply(message, "model-1", Usage) };
        }

        public Task<AiChoice> ResolveAsync(string alias, Guid? scopeId = null, string? dataClass = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
