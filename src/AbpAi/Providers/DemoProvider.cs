using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AbpAi.Providers;

/// <summary>
/// The demo model: answers through the host's <see cref="IAiDemoResponder"/>s, first that answers wins, so features
/// that use AI run in tests and evals with no provider. With none answering it says it's the demo model (or answers
/// <c>{}</c> when a schema was asked for). Tokens are counted as the gateway estimates them; it costs what its
/// connection's prices say.
/// </summary>
public sealed class DemoProvider(IEnumerable<IAiDemoResponder> responders, IOptions<AiOptions> options) : IAiProvider
{
    public string Kind => "demo";

    public async Task<AiProviderReply> CompleteAsync(AiProviderCall call, CancellationToken cancellationToken)
    {
        JsonElement message = await ReplyAsync(call, cancellationToken);
        int perToken = Math.Max(1, options.Value.CharsPerToken);
        int input = (call.Messages.GetRawText().Length + AiService.SystemChars(call.System, call.SystemBlocks) + (call.Tools?.GetRawText().Length ?? 0)) / perToken + 1;
        int output = Math.Min(call.MaxTokens, message.GetRawText().Length / perToken + 1);
        return new AiProviderReply(message, input, output);
    }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiProviderCall call, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AiProviderReply reply = await CompleteAsync(call, cancellationToken);
        foreach (AiStreamEvent e in Events(reply, call.Target.Model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return e;
        }
    }

    private async Task<JsonElement> ReplyAsync(AiProviderCall call, CancellationToken ct)
    {
        foreach (IAiDemoResponder responder in responders)
        {
            if (await responder.RespondAsync(call, ct) is { } reply)
            {
                return Normalised(reply);
            }
        }

        if (call.OutputSchema is not null)
        {
            return AiMessages.AssistantText("{}");
        }

        string asked = AiMessages.LastUserText(call.Messages);
        return AiMessages.AssistantText($"This is the demo model; no provider answered. You asked: {(asked.Length > 200 ? asked[..200] + "…" : asked)}");
    }

    /// <summary>A responder's reply with a role and a stop reason, whatever it left out.</summary>
    private static JsonElement Normalised(JsonElement reply)
    {
        if (reply.ValueKind == JsonValueKind.String)
        {
            return AiMessages.AssistantText(reply.GetString() ?? "");
        }

        System.Text.Json.Nodes.JsonObject message = System.Text.Json.Nodes.JsonNode.Parse(reply.GetRawText())!.AsObject();
        message["role"] ??= "assistant";
        if (message["stop_reason"] is null)
        {
            bool tools = message["content"] is System.Text.Json.Nodes.JsonArray blocks && blocks.Any(b => b?["type"]?.GetValue<string>() == "tool_use");
            message["stop_reason"] = tools ? "tool_use" : "end_turn";
        }

        return JsonSerializer.SerializeToElement(message);
    }

    /// <summary>A whole reply as the Messages API streams one: text in pieces, a tool's input in one.</summary>
    internal static IEnumerable<AiStreamEvent> Events(AiProviderReply reply, string model)
    {
        static AiStreamEvent E(string type, object data) => new(type, JsonSerializer.SerializeToElement(data));

        yield return E("message_start", new
        {
            type = "message_start",
            message = new { id = "msg_demo_" + Guid.NewGuid().ToString("N")[..12], type = "message", role = "assistant", model, content = Array.Empty<object>(),
                usage = new { input_tokens = reply.InputTokens, output_tokens = 0 } },
        });
        int index = 0;
        if (reply.Message.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement block in content.EnumerateArray())
            {
                string type = block.GetProperty("type").GetString() ?? "text";
                if (type == "text")
                {
                    yield return E("content_block_start", new { type = "content_block_start", index, content_block = new { type = "text", text = "" } });
                    string text = block.GetProperty("text").GetString() ?? "";
                    for (int at = 0; at < text.Length; at += 24)
                    {
                        yield return E("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "text_delta", text = text.Substring(at, Math.Min(24, text.Length - at)) } });
                    }
                }
                else if (type == "tool_use")
                {
                    yield return E("content_block_start", new
                    {
                        type = "content_block_start", index,
                        content_block = new { type = "tool_use", id = block.GetProperty("id").GetString(), name = block.GetProperty("name").GetString(), input = new { } },
                    });
                    yield return E("content_block_delta", new
                    {
                        type = "content_block_delta", index, delta = new { type = "input_json_delta", partial_json = block.GetProperty("input").GetRawText() },
                    });
                }
                else
                {
                    yield return new AiStreamEvent("content_block_start", JsonSerializer.SerializeToElement(new { type = "content_block_start", index, content_block = block }));
                }

                yield return E("content_block_stop", new { type = "content_block_stop", index });
                index++;
            }
        }

        string stop = reply.Message.TryGetProperty("stop_reason", out JsonElement s) ? s.GetString() ?? "end_turn" : "end_turn";
        yield return E("message_delta", new { type = "message_delta", delta = new { stop_reason = stop }, usage = new { output_tokens = reply.OutputTokens } });
        yield return E("message_stop", new { type = "message_stop" });
    }
}
