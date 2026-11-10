# Testing with the demo model

Set up a connection with provider `demo` and point your aliases at it. The demo model answers through
`IAiDemoResponder`s, first that answers wins; with none answering it says it's the demo model (or answers `{}` when a
schema was asked for). It counts tokens as the gateway estimates them, never more than `MaxTokens`, and streams like a
real provider.

## `AiScript`

The package registers one `AiScript` (a singleton) as a responder. Take it from the container and tell it what to say:

```csharp
AiScript script = services.GetRequiredService<AiScript>();
script.EnqueueText("Revenue rose.");                         // the next call
script.OnText("churned", "Three customers churned.");         // calls whose last question says "churned"
script.OnPurpose("agent:ask", c => AiMessages.AssistantToolUse("run_query", new { sql = "select 1" }), once: true);
```

`script.Calls` is every call the demo model was asked, so a test can check what was sent, and where.

## A question set from a file

An eval keeps its questions and their scripted replies in JSON beside it:

```json
[
  { "when": { "purpose": "agent:ask", "contains": "revenue" }, "tool": { "name": "run_query", "input": { "sql": "..." } }, "once": true },
  { "when": { "purpose": "agent:ask", "contains": "revenue" }, "reply": "Revenue by region: North 120, South 80." }
]
```

`script.Load(json)` adds them. `contains` looks at the last user message that has text, so a tool's answer doesn't
hide the question. `tests/AbpAi.Host.Tests` runs a question set through `AbpAi.Agents`, the gateway and the demo
model, and checks each answer and where each question went: that's what AbpAgent's eval harness (A8) runs in CI.
