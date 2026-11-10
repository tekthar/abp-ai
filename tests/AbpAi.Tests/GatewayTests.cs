using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AbpAi.Agents;
using AbpAi.Providers;
using Microsoft.Extensions.Options;
using Toda.Agents;

namespace AbpAi.Tests;

public class GatewayTests
{
    private static readonly AiDataRules Rules = new(Options.Create(new AiOptions()));

    [Fact]
    public void A_call_that_states_no_class_or_an_unknown_one_counts_as_the_highest()
    {
        Assert.Equal(AiDataClasses.Sensitive, Rules.ClassOf(null));
        Assert.Equal(AiDataClasses.Sensitive, Rules.ClassOf("secret-ish"));
        Assert.Equal(AiDataClasses.Rows, Rules.ClassOf(" Rows "));
    }

    [Fact]
    public void A_connection_receives_only_the_classes_it_lists()
    {
        AiConnection aggregates = new(Guid.NewGuid(), null, "public", "demo") { DataClasses = [AiDataClasses.Public, AiDataClasses.Aggregates] };
        Assert.True(AiDataRules.Allows(aggregates, AiDataClasses.Aggregates));
        Assert.False(AiDataRules.Allows(aggregates, AiDataClasses.Rows));
        Assert.Throws<Volo.Abp.UserFriendlyException>(() => Rules.Checked(["rows", "nonsense"]));
    }

    [Fact]
    public void Periods_start_at_midnight_on_Monday_and_on_the_first()
    {
        DateTime thursday = new(2026, 11, 12, 15, 30, 0, DateTimeKind.Utc);
        Assert.Equal((new DateTime(2026, 11, 12), new DateTime(2026, 11, 13)), AiPeriods.Of(AiPeriods.Day, thursday));
        Assert.Equal((new DateTime(2026, 11, 9), new DateTime(2026, 11, 16)), AiPeriods.Of(AiPeriods.Week, thursday));
        Assert.Equal((new DateTime(2026, 11, 1), new DateTime(2026, 12, 1)), AiPeriods.Of(AiPeriods.Month, thursday));
        Assert.Equal(new DateTime(2026, 11, 9), AiPeriods.Of(AiPeriods.Week, new DateTime(2026, 11, 15, 23, 0, 0, DateTimeKind.Utc)).Start);
    }

    [Fact]
    public void A_scope_chain_knows_how_near_each_scope_is()
    {
        Guid project = Guid.NewGuid(), department = Guid.NewGuid(), other = Guid.NewGuid();
        AiScopeChain chain = new([project, department]);
        Assert.Equal(0, chain.Nearness(project));
        Assert.Equal(1, chain.Nearness(department));
        Assert.Equal(2, chain.Nearness(null));
        Assert.False(chain.Sees(other));
        Assert.Equal([department, project], chain.Path);
    }

    [Fact]
    public void The_Anthropic_request_is_the_call_as_it_is_with_a_schema_as_output_config()
    {
        AiProviderCall call = Call(AiMessages.User("Totals by region?"), system: "Be brief.", schema: Json("""{"type":"object"}"""));
        JsonObject body = AnthropicProvider.Body(call, stream: true);
        Assert.Equal("claude-opus-5-5", body["model"]!.GetValue<string>());
        Assert.Equal("Be brief.", body["system"]!.GetValue<string>());
        Assert.Equal("json_schema", body["output_config"]!["format"]!["type"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
    }

    [Fact]
    public void A_conversation_with_tools_is_translated_to_chat_completions_and_back()
    {
        JsonElement messages = Json("""
            [
              {"role":"user","content":"How many orders?"},
              {"role":"assistant","content":[{"type":"text","text":"Let me count."},{"type":"tool_use","id":"t1","name":"run_query","input":{"sql":"select 1"}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"42","is_error":false},{"type":"text","text":"And now?"}]}
            ]
            """);
        JsonElement tools = Json("""[{"name":"run_query","description":"Runs SQL","input_schema":{"type":"object"}}]""");
        JsonObject body = OpenAiCompatibleProvider.Body(Call(messages, system: "You answer questions.", tools: tools) with { }, stream: false);
        JsonArray sent = body["messages"]!.AsArray();
        Assert.Equal(["system", "user", "assistant", "tool", "user"], sent.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("run_query", sent[2]!["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("t1", sent[3]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("function", body["tools"]![0]!["type"]!.GetValue<string>());

        AiProviderReply reply = OpenAiCompatibleProvider.FromCompletion(Json("""
            {"model":"gpt-x","choices":[{"finish_reason":"tool_calls","message":{"content":null,"tool_calls":[{"id":"c9","type":"function","function":{"name":"run_query","arguments":"{\"sql\":\"select 2\"}"}}]}}],
             "usage":{"prompt_tokens":11,"completion_tokens":7}}
            """));
        Assert.Equal("tool_use", reply.Message.GetProperty("stop_reason").GetString());
        Assert.Equal("select 2", reply.Message.GetProperty("content")[0].GetProperty("input").GetProperty("sql").GetString());
        Assert.Equal((11, 7), (reply.InputTokens, reply.OutputTokens));
    }

    [Fact]
    public void A_chat_completions_stream_becomes_the_messages_apis_events_and_the_whole_message()
    {
        OpenAiCompatibleProvider.StreamTranslator translator = new("gpt-x");
        List<AiStreamEvent> events = [];
        foreach (string chunk in new[]
                 {
                     """{"id":"c1","choices":[{"delta":{"content":"Hel"}}]}""",
                     """{"id":"c1","choices":[{"delta":{"content":"lo"}}]}""",
                     """{"id":"c1","choices":[{"delta":{"tool_calls":[{"index":0,"id":"t1","function":{"name":"run_query","arguments":"{\"sql\":"}}]}}]}""",
                     """{"id":"c1","choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"select 1\"}"}}]},"finish_reason":"tool_calls"}]}""",
                     """{"id":"c1","choices":[],"usage":{"prompt_tokens":5,"completion_tokens":9}}""",
                 })
        {
            events.AddRange(translator.Add(Json(chunk)));
        }

        events.AddRange(translator.End());
        AiMessageBuilder builder = new();
        events.ForEach(builder.Add);

        Assert.Equal("message_start", events[0].Type);
        Assert.Equal("message_stop", events[^1].Type);
        JsonElement message = builder.Message;
        Assert.Equal("Hello", message.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("select 1", message.GetProperty("content")[1].GetProperty("input").GetProperty("sql").GetString());
        Assert.Equal("tool_use", message.GetProperty("stop_reason").GetString());
        Assert.Equal((5, 9), (builder.InputTokens, builder.OutputTokens));
    }

    [Fact]
    public async Task The_demo_model_streams_what_it_would_have_answered_whole()
    {
        AiScript script = new AiScript().Enqueue(AiMessages.AssistantToolUse("run_query", new { sql = "select 3" }, "t7", "Counting."));
        DemoProvider demo = new([script], Options.Create(new AiOptions()));
        List<AiStreamEvent> events = [];
        await foreach (AiStreamEvent e in demo.StreamAsync(Call(AiMessages.User("Count")), CancellationToken.None))
        {
            events.Add(e);
        }

        AiMessageBuilder builder = new();
        events.ForEach(builder.Add);
        Assert.Equal("Counting.", AiMessages.TextOf(builder.Message));
        Assert.Equal("t7", builder.Message.GetProperty("content")[1].GetProperty("id").GetString());
        Assert.Equal("select 3", builder.Message.GetProperty("content")[1].GetProperty("input").GetProperty("sql").GetString());
        Assert.Equal("tool_use", builder.Message.GetProperty("stop_reason").GetString());
    }

    [Fact]
    public async Task A_script_answers_by_purpose_and_words_and_once_when_it_says_so()
    {
        AiScript script = new AiScript().Load("""
            [
              { "when": { "purpose": "agent:*", "contains": "revenue" }, "tool": { "name": "run_query", "input": { "sql": "select sum(x)" } }, "once": true },
              { "when": { "contains": "revenue" }, "reply": "Revenue was 42." }
            ]
            """);
        AiProviderCall agentCall = Call(AiMessages.User("What was revenue?")) with { Purpose = "agent:ask" };
        JsonElement first = (await script.RespondAsync(agentCall, CancellationToken.None))!.Value;
        JsonElement second = (await script.RespondAsync(agentCall, CancellationToken.None))!.Value;
        Assert.Equal("tool_use", first.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("Revenue was 42.", AiMessages.TextOf(second));
        Assert.Null(await script.RespondAsync(Call(AiMessages.User("Hi")), CancellationToken.None));
        Assert.Equal(3, script.Calls.Count);
    }

    [Fact]
    public void Structured_replies_are_read_as_json_fenced_or_not()
    {
        Assert.Equal(3, AiMessages.TryParseJson("```json\n{\"a\":3}\n```")!.Value.GetProperty("a").GetInt32());
        Assert.Null(AiMessages.TryParseJson("Sure! Here it is."));
    }

    [Fact]
    public async Task An_agents_call_goes_through_the_gateway_and_a_refusal_ends_the_run_with_its_code()
    {
        RecordingAi ai = new();
        AiAgentModels models = new(ai, new FixedCalls(new AiAgentCallInfo(Guid.Parse("00000000-0000-0000-0000-0000000000b1"), "aggregates")));
        Guid agent = Guid.NewGuid(), scope = Guid.NewGuid(), item = Guid.NewGuid();
        AgentModelRequest request = new(agent, scope, "smart", AiMessages.User("hi"), "You are Ask.", null, 2000, "agent:ask", item);

        AgentModelReply reply = await models.CompleteAsync(request, CancellationToken.None);
        AiRequest sent = ai.Sent.Single();
        Assert.Equal(("smart", agent, scope, "agent:ask", "aggregates"), (sent.Alias, sent.AccountId!.Value, sent.ScopeId!.Value, sent.Purpose, sent.DataClass));
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-0000000000b1"), sent.OnBehalfOf);
        Assert.Equal(new AiSubject("item", item.ToString()), sent.About);
        Assert.Equal(("demo", 3, 4, 0.5m), (reply.Model, reply.InputTokens, reply.OutputTokens, reply.Cost));

        ai.Refuse = new AiRefusedException(AiRefusalCodes.OverLimit, "Your daily AI limit is reached.");
        AgentModelRefusedException refused = await Assert.ThrowsAsync<AgentModelRefusedException>(() => models.CompleteAsync(request, CancellationToken.None));
        Assert.Equal(AiRefusalCodes.OverLimit, refused.Code);
    }

    [Fact]
    public void The_contracts_are_plain_dotnet_and_nothing_carries_the_old_name()
    {
        Assembly contracts = typeof(IAi).Assembly;
        Assert.All(contracts.GetReferencedAssemblies(), a => Assert.True(a.Name!.StartsWith("System", StringComparison.Ordinal) || a.Name == "netstandard",
            $"AbpAi.Contracts references {a.Name}."));

        foreach (Assembly assembly in new[] { contracts, typeof(AiService).Assembly })
        {
            Assert.All(assembly.GetTypes(), t => Assert.DoesNotContain("Toda", t.FullName ?? "", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name!.Contains("Toda", StringComparison.OrdinalIgnoreCase));
        }

        // The ledger's lock spells the gateway's own name, not a product's.
        Assert.Equal("AI LEDGR", System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(AiLedger.LockKey).Reverse().ToArray()));
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static AiProviderCall Call(JsonElement messages, string? system = null, JsonElement? tools = null, JsonElement? schema = null) =>
        new(new AiTarget(Guid.NewGuid(), "test", "", "key", "claude-opus-5-5", new Dictionary<string, string>()), messages, system, tools, 100, schema);

    private sealed class FixedCalls(AiAgentCallInfo info) : IAiAgentCalls
    {
        public Task<AiAgentCallInfo> DescribeAsync(AgentModelRequest request, CancellationToken cancellationToken) => Task.FromResult(info);
    }

    private sealed class RecordingAi : IAi
    {
        public List<AiRequest> Sent { get; } = [];

        public AiRefusedException? Refuse { get; set; }

        public Task<AiReply> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            Sent.Add(request);
            return Refuse is not null ? throw Refuse : Task.FromResult(new AiReply(AiMessages.AssistantText("ok"), "demo", new AiUsage(3, 4, 0.5m)));
        }

        public IAsyncEnumerable<AiStreamEvent> StreamAsync(AiRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AiChoice> ResolveAsync(string alias, Guid? scopeId = null, string? dataClass = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
