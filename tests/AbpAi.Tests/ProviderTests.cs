using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AbpAi.Agents;
using AbpAi.Providers;
using AbpAgent;
using Microsoft.Extensions.Options;

namespace AbpAi.Tests;

/// <summary>
/// 0.4.0's providers: the provider's own reason in an error, with no key in it; a busy provider tried again; a call's
/// start marked for the cache, and cached tokens read; search results that reach a Chat Completions model; the demo
/// model's stream as a model writes; and the agents' bridge that caches, floors its tokens and ends a run a model
/// declines.
/// </summary>
public class ProviderTests
{
    [Fact]
    public void An_errors_reason_is_the_providers_own_words_with_no_key_in_them()
    {
        Assert.Equal("invalid x-api-key", ProviderHttp.Reason("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", null));
        Assert.Equal("model 'llama9' not found", ProviderHttp.Reason("""{"error":"model 'llama9' not found"}""", null));
        Assert.Equal("Bad gateway", ProviderHttp.Reason("Bad gateway", null));
        Assert.Equal("it gave no reason", ProviderHttp.Reason("  ", null));

        // The connection's key, and anything shaped like a key, whole or partly hidden.
        string reason = ProviderHttp.Reason("""{"error":{"message":"Incorrect API key provided: sk-proj-****wxyz. Key my-own-secret-key."}}""", "my-own-secret-key");
        Assert.Equal("Incorrect API key provided: [key]. Key [key].", reason);
        Assert.Equal("Anthropic answered 401: [key] is wrong", ProviderHttp.Failed("Anthropic", 401, """{"error":{"message":"sk-ant-api03-abcdef is wrong"}}""", null).Message);
    }

    [Fact]
    public async Task A_busy_provider_is_tried_again_after_its_retry_after_and_then_answers()
    {
        int tries = 0;
        SequenceHttp http = new(_ =>
        {
            tries++;
            if (tries == 1)
            {
                HttpResponseMessage busy = new((HttpStatusCode)529) { Content = new StringContent("""{"error":{"message":"Overloaded"}}""") };
                busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
                return busy;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Message("Hello.")) };
        });
        AnthropicProvider provider = new(http, Options.Create(new AiOptions()));
        AiProviderReply reply = await provider.CompleteAsync(Call(AiMessages.User("Hi")), CancellationToken.None);
        Assert.Equal((2, "Hello."), (tries, AiMessages.TextOf(reply.Message)));

        // With no tries left, the provider's own reason, and its status.
        tries = 0;
        SequenceHttp alwaysBusy = new(_ => { tries++; return new HttpResponseMessage((HttpStatusCode)529) { Content = new StringContent("""{"error":{"message":"Overloaded"}}""") }; });
        AiProviderException failed = await Assert.ThrowsAsync<AiProviderException>(() =>
            new AnthropicProvider(alwaysBusy, Options.Create(new AiOptions { ProviderRetries = 1 })).CompleteAsync(Call(AiMessages.User("Hi")), CancellationToken.None));
        Assert.Equal((2, (int?)529, "Overloaded"), (tries, failed.Status, failed.Reason));

        // A wrong key isn't tried again.
        tries = 0;
        SequenceHttp unauthorised = new(_ => { tries++; return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") }; });
        await Assert.ThrowsAsync<AiProviderException>(() => new AnthropicProvider(unauthorised).CompleteAsync(Call(AiMessages.User("Hi")), CancellationToken.None));
        Assert.Equal(1, tries);
    }

    [Fact]
    public void A_call_that_may_be_cached_marks_its_system_prompt_and_its_last_block_unless_it_marked_its_own()
    {
        JsonObject body = AnthropicProvider.Body(Call(AiMessages.User("Totals?"), system: "Be brief.") with { Cache = true }, stream: false);
        Assert.Equal("ephemeral", (string?)body["system"]![0]!["cache_control"]!["type"]);
        Assert.Equal("ephemeral", (string?)body["messages"]![0]!["content"]![0]!["cache_control"]!["type"]);
        Assert.Equal("Totals?", (string?)body["messages"]![0]!["content"]![0]!["text"]);

        // Its own marks are left as they are; and a call that may not be cached has none.
        JsonElement marked = Json("""[{"role":"user","content":[{"type":"text","text":"a","cache_control":{"type":"ephemeral","ttl":"1h"}},{"type":"text","text":"b"}]}]""");
        JsonObject own = AnthropicProvider.Body(Call(marked) with { Cache = true }, stream: false);
        Assert.Null(own["messages"]![0]!["content"]![1]!["cache_control"]);
        Assert.DoesNotContain("cache_control", AnthropicProvider.Body(Call(AiMessages.User("Totals?"), system: "Be brief."), stream: false).ToJsonString());
    }

    [Fact]
    public async Task Cached_tokens_are_read_from_both_providers_and_kept_out_of_the_input()
    {
        string anthropic = """{"role":"assistant","content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":12,"output_tokens":3,"cache_creation_input_tokens":800,"cache_read_input_tokens":4000}}""";
        AiProviderReply fromAnthropic = await new AnthropicProvider(new SequenceHttp(_ => Ok(anthropic))).CompleteAsync(Call(AiMessages.User("Hi")), CancellationToken.None);
        Assert.Equal((12, 3, 800, 4000), (fromAnthropic.InputTokens, fromAnthropic.OutputTokens, fromAnthropic.CacheWriteTokens, fromAnthropic.CacheReadTokens));

        string openAi = """{"model":"gpt","choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5000,"completion_tokens":7,"prompt_tokens_details":{"cached_tokens":4096}}}""";
        AiProviderReply fromOpenAi = await new OpenAiCompatibleProvider(new SequenceHttp(_ => Ok(openAi))).CompleteAsync(Call(AiMessages.User("Hi")), CancellationToken.None);
        Assert.Equal((904, 7, 0, 4096), (fromOpenAi.InputTokens, fromOpenAi.OutputTokens, fromOpenAi.CacheWriteTokens, fromOpenAi.CacheReadTokens));

        // A stream's start counts what was cached; the message it builds keeps it.
        AiMessageBuilder builder = new();
        builder.Add(new AiStreamEvent("message_start", Json("""{"type":"message_start","message":{"usage":{"input_tokens":9,"output_tokens":1,"cache_creation_input_tokens":10,"cache_read_input_tokens":20}}}""")));
        builder.Add(new AiStreamEvent("message_delta", Json("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":6}}""")));
        Assert.Equal((9, 6, 10, 20), (builder.InputTokens, builder.OutputTokens, builder.CacheWriteTokens, builder.CacheReadTokens));
    }

    [Fact]
    public void Search_results_reach_a_Chat_Completions_model_as_text()
    {
        JsonElement messages = Json("""
            [{"role":"user","content":[
              {"type":"search_result","source":"https://kb.example/vpn","title":"VPN setup","content":[{"type":"text","text":"Use the new client."}]},
              {"type":"text","text":"How do I connect?"}]},
             {"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"search","input":{}}]},
             {"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":[
              {"type":"search_result","source":"https://kb.example/mfa","title":"MFA","content":[{"type":"text","text":"Approve on your phone."}]}]}]}]
            """);
        JsonObject body = OpenAiCompatibleProvider.Body(Call(messages), stream: false);
        string user = (string)body["messages"]![0]!["content"]!;
        Assert.Contains("Source: https://kb.example/vpn", user);
        Assert.Contains("Title: VPN setup", user);
        Assert.Contains("Use the new client.", user);
        Assert.EndsWith("How do I connect?", user);
        Assert.Contains("Approve on your phone.", (string)body["messages"]![2]!["content"]!);
    }

    [Fact]
    public async Task The_demo_models_stream_comes_as_a_model_writes_it()
    {
        AiScript script = new();
        script.Enqueue(Json("""
            {"role":"assistant","content":[
              {"type":"thinking","thinking":"Let me look at the totals first.","signature":"sig-1"},
              {"type":"text","text":"Sales rose in every region this quarter.","citations":[{"type":"search_result_location","source":"q3","cited_text":"rose"}]},
              {"type":"tool_use","id":"t1","name":"chart","input":{"kind":"bar","series":["north","south","east","west"]}}]}
            """));
        DemoProvider demo = new([script], Options.Create(new AiOptions()));
        AiProviderCall call = Call(AiMessages.User("How did sales do?")) with { Target = new AiTarget(Guid.NewGuid(), "demo", "", null, "house-model", new Dictionary<string, string>()) };
        List<AiStreamEvent> events = [];
        await foreach (AiStreamEvent e in demo.StreamAsync(call, CancellationToken.None))
        {
            events.Add(e);
        }

        Assert.Equal("house-model", events[0].Data.GetProperty("message").GetProperty("model").GetString());
        List<string> deltas = [.. events.Where(e => e.Type == "content_block_delta").Select(e => e.Data.GetProperty("delta").GetProperty("type").GetString()!)];
        Assert.Equal(["thinking_delta", "thinking_delta", "signature_delta"], deltas.TakeWhile(d => d != "citations_delta"));
        Assert.Contains("citations_delta", deltas);
        Assert.True(deltas.Count(d => d == "text_delta") > 1, "text comes in pieces");
        Assert.Equal("message_stop", events[^1].Type);

        // The whole message they build is the one the responder gave.
        AiMessageBuilder builder = new();
        events.ForEach(builder.Add);
        JsonElement built = builder.Message;
        Assert.Equal("Sales rose in every region this quarter.", AiMessages.TextOf(built));
        Assert.Equal(4, built.GetProperty("content")[2].GetProperty("input").GetProperty("series").GetArrayLength());
        Assert.Equal("sig-1", built.GetProperty("content")[0].GetProperty("signature").GetString());
    }

    [Fact]
    public async Task An_agents_turns_are_cached_get_at_least_the_hosts_tokens_and_a_declined_answer_ends_the_run()
    {
        ReplyingAi ai = new();
        AgentModelRequest request = new(Guid.NewGuid(), Guid.NewGuid(), "smart", AiMessages.User("hi"), "You are Ask.", null, 4000, "agent:ask", null);
        await new AiAgentModels(ai, new FixedCalls(new AiAgentCallInfo(null, null) { MaxTokens = 16000 })).CompleteAsync(request, CancellationToken.None);
        Assert.Equal((true, 16000), (ai.Sent[^1].Cache, ai.Sent[^1].MaxTokens));

        // A host's floor below AbpAgent's leaves AbpAgent's; caching may be turned off.
        await new AiAgentModels(ai, new FixedCalls(new AiAgentCallInfo(null, null) { MaxTokens = 1000, Cache = false })).CompleteAsync(request, CancellationToken.None);
        Assert.Equal((false, 4000), (ai.Sent[^1].Cache, ai.Sent[^1].MaxTokens));

        ai.StopReason = "refusal";
        AgentModelRefusedException declined = await Assert.ThrowsAsync<AgentModelRefusedException>(() =>
            new AiAgentModels(ai, new FixedCalls(new AiAgentCallInfo(null, null))).CompleteAsync(request, CancellationToken.None));
        Assert.Equal(AiAgentModels.RefusalStop, declined.Code);
        Assert.Contains("declined", declined.Message);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Message(string text) =>
        JsonSerializer.Serialize(new { role = "assistant", content = new[] { new { type = "text", text } }, usage = new { input_tokens = 3, output_tokens = 2 } });

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static AiProviderCall Call(JsonElement messages, string? system = null) =>
        new(new AiTarget(Guid.NewGuid(), "test", "", "key", "model-1", new Dictionary<string, string>()), messages, system, null, 100, null);

    /// <summary>An HTTP client whose answers a function gives, one request at a time.</summary>
    private sealed class SequenceHttp(Func<HttpRequestMessage, HttpResponseMessage> answer) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(answer));

        private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
        }
    }

    private sealed class FixedCalls(AiAgentCallInfo info) : IAiAgentCalls
    {
        public Task<AiAgentCallInfo> DescribeAsync(AgentModelRequest request, CancellationToken cancellationToken) => Task.FromResult(info);
    }

    /// <summary>A gateway that answers every call with a stop reason of its choosing, and keeps what it was asked.</summary>
    private sealed class ReplyingAi : IAi
    {
        public List<AiRequest> Sent { get; } = [];

        public string StopReason { get; set; } = "end_turn";

        public Task<AiReply> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            JsonElement message = JsonSerializer.SerializeToElement(new { role = "assistant", content = new[] { new { type = "text", text = "" } }, stop_reason = StopReason });
            return Task.FromResult(new AiReply(message, "model-1", new AiUsage(1, 1, 0)));
        }

        public IAsyncEnumerable<AiStreamEvent> StreamAsync(AiRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AiChoice> ResolveAsync(string alias, Guid? scopeId = null, string? dataClass = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
