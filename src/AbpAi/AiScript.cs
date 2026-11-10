using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AbpAi;

/// <summary>
/// Scripted replies for the demo model (AI5): what tests and evals use in place of a provider. Rules are asked in the
/// order they were added; a rule may match on the call's purpose, its model, or words in the last user
/// message, and may answer once or every time. Replies queued with <see cref="Enqueue"/> answer the next calls first,
/// in order. A script can also be loaded from JSON (<see cref="Load"/>), so an eval's question set and its replies
/// live in files beside it.
/// </summary>
public sealed class AiScript : IAiDemoResponder
{
    private readonly ConcurrentQueue<JsonElement> _queued = new();
    private readonly List<Rule> _rules = [];
    private readonly ConcurrentQueue<AiProviderCall> _calls = new();
    private readonly Lock _lock = new();

    /// <summary>Every call the demo model was asked, in order: what a test checks was sent.</summary>
    public IReadOnlyList<AiProviderCall> Calls => [.. _calls];

    /// <summary>Answers the next call with this message (or text), ahead of the rules.</summary>
    public AiScript Enqueue(JsonElement message)
    {
        _queued.Enqueue(message);
        return this;
    }

    public AiScript EnqueueText(string text) => Enqueue(AiMessages.AssistantText(text));

    /// <summary>Answers calls that match.</summary>
    public AiScript On(Func<AiProviderCall, bool> when, Func<AiProviderCall, JsonElement> reply, bool once = false)
    {
        lock (_lock)
        {
            _rules.Add(new Rule(when, reply, once));
        }

        return this;
    }

    /// <summary>Answers calls whose last user message contains the words, with the text.</summary>
    public AiScript OnText(string contains, string text, bool once = false) =>
        On(c => AiMessages.LastUserText(c.Messages).Contains(contains, StringComparison.OrdinalIgnoreCase), _ => AiMessages.AssistantText(text), once);

    /// <summary>Answers calls with the purpose (<c>agent:analyst</c>; a trailing <c>*</c> matches its start).</summary>
    public AiScript OnPurpose(string purpose, Func<AiProviderCall, JsonElement> reply, bool once = false) =>
        On(c => purpose.EndsWith('*') ? c.Purpose.StartsWith(purpose[..^1], StringComparison.Ordinal) : c.Purpose == purpose, reply, once);

    /// <summary>Forgets the rules, the queue and the calls.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _rules.Clear();
        }

        _queued.Clear();
        _calls.Clear();
    }

    /// <summary>
    /// Adds the rules of a JSON script: <c>[{ "when": { "purpose", "contains", "model" }, "reply": "text" }]</c>, where a
    /// reply may instead be <c>"tool": { "name", "input" }</c> or a whole <c>"message"</c>, and <c>"once": true</c>
    /// answers once.
    /// </summary>
    public AiScript Load(string json)
    {
        foreach (ScriptRule rule in JsonSerializer.Deserialize<List<ScriptRule>>(json, JsonSerializerOptions.Web) ?? [])
        {
            JsonElement reply = rule.Message
                ?? (rule.Tool is { } tool ? AiMessages.AssistantToolUse(tool.Name, tool.Input ?? JsonSerializer.SerializeToElement(new { }), text: rule.Reply)
                    : AiMessages.AssistantText(rule.Reply ?? ""));
            ScriptWhen when = rule.When ?? new ScriptWhen();
            On(c => (when.Purpose is null || (when.Purpose.EndsWith('*') ? c.Purpose.StartsWith(when.Purpose[..^1], StringComparison.Ordinal) : c.Purpose == when.Purpose))
                    && (when.Model is null || c.Target.Model == when.Model)
                    && (when.Contains is null || AiMessages.LastUserText(c.Messages).Contains(when.Contains, StringComparison.OrdinalIgnoreCase)),
                _ => reply, rule.Once);
        }

        return this;
    }

    public Task<JsonElement?> RespondAsync(AiProviderCall call, CancellationToken cancellationToken)
    {
        _calls.Enqueue(call);
        if (_queued.TryDequeue(out JsonElement queued))
        {
            return Task.FromResult<JsonElement?>(queued);
        }

        lock (_lock)
        {
            foreach (Rule rule in _rules)
            {
                if (!rule.Used && rule.When(call))
                {
                    if (rule.Once)
                    {
                        rule.Used = true;
                    }

                    return Task.FromResult<JsonElement?>(rule.Reply(call));
                }
            }
        }

        return Task.FromResult<JsonElement?>(null);
    }

    private sealed class Rule(Func<AiProviderCall, bool> when, Func<AiProviderCall, JsonElement> reply, bool once)
    {
        public Func<AiProviderCall, bool> When { get; } = when;
        public Func<AiProviderCall, JsonElement> Reply { get; } = reply;
        public bool Once { get; } = once;
        public bool Used { get; set; }
    }

    private sealed record ScriptWhen(string? Purpose = null, string? Contains = null, string? Model = null);

    private sealed record ScriptTool(string Name, JsonElement? Input);

    private sealed record ScriptRule(ScriptWhen? When, string? Reply, ScriptTool? Tool, JsonElement? Message, [property: JsonPropertyName("once")] bool Once = false);
}
