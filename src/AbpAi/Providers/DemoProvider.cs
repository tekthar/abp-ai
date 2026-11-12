using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace AbpAi.Providers;

/// <summary>
/// The demo model: answers through the host's <see cref="IAiDemoResponder"/>s, first that answers wins, so features
/// that use AI run in tests and evals with no provider. With none answering it says it's the demo model (or answers
/// <c>{}</c> when a schema was asked for). Tokens are counted as the gateway estimates them; it costs what its
/// connection's prices say. Its reply names the model the call went to, and a streamed one comes as a model writes:
/// a text block's citations, then its text in small pieces; thinking, then its signature; a tool's input in pieces;
/// with <see cref="AiOptions.DemoPacing"/> between them, so a live view can be seen being written.
/// </summary>
public sealed class DemoProvider(IEnumerable<IAiDemoResponder> responders, IOptions<AiOptions> options) : IAiProvider
{
    /// <summary>The one model the demo model lists when its models are asked for; a demo connection may name its models as it likes.</summary>
    public const string ModelId = "demo";

    /// <summary>The characters of text or thinking in one streamed piece.</summary>
    public const int TextPiece = 24;

    /// <summary>The characters of a tool's input in one streamed piece.</summary>
    public const int InputPiece = 90;

    public string Kind => "demo";

    public Task<IReadOnlyList<string>?> ListModelsAsync(AiTarget target, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>?>([ModelId]);

    public async Task<AiProviderReply> CompleteAsync(AiProviderCall call, CancellationToken cancellationToken)
    {
        JsonElement answer = await ReplyAsync(call, cancellationToken);
        int perToken = Math.Max(1, options.Value.CharsPerToken);
        int input = (call.Messages.GetRawText().Length + AiService.SystemChars(call.System, call.SystemBlocks) + (call.Tools?.GetRawText().Length ?? 0)) / perToken + 1;
        int output = Math.Min(call.MaxTokens, answer.GetRawText().Length / perToken + 1);
        JsonObject message = JsonNode.Parse(answer.GetRawText())!.AsObject();
        message["model"] ??= string.IsNullOrEmpty(call.Target.Model) ? ModelId : call.Target.Model;
        message["usage"] = new JsonObject { ["input_tokens"] = input, ["output_tokens"] = output };
        return new AiProviderReply(JsonSerializer.SerializeToElement(message), input, output);
    }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiProviderCall call, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AiProviderReply reply = await CompleteAsync(call, cancellationToken);
        TimeSpan pacing = options.Value.DemoPacing;
        foreach (AiStreamEvent e in Events(reply, call.Target.Model))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return e;
            if (pacing > TimeSpan.Zero && e.Type == "content_block_delta")
            {
                await Task.Delay(pacing, cancellationToken);
            }
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

        JsonObject message = JsonNode.Parse(reply.GetRawText())!.AsObject();
        message["role"] ??= "assistant";
        if (message["stop_reason"] is null)
        {
            bool tools = message["content"] is JsonArray blocks && blocks.Any(b => b?["type"]?.GetValue<string>() == "tool_use");
            message["stop_reason"] = tools ? "tool_use" : "end_turn";
        }

        return JsonSerializer.SerializeToElement(message);
    }

    /// <summary>
    /// A whole reply as the Messages API streams one: a text block's citations first, then its text in pieces; thinking
    /// in pieces, then its signature; a tool's input in pieces; redacted thinking and any other block whole.
    /// </summary>
    public static IEnumerable<AiStreamEvent> Events(AiProviderReply reply, string model)
    {
        static AiStreamEvent E(string type, JsonObject data) => new(type, JsonSerializer.SerializeToElement(data));
        static AiStreamEvent Delta(int index, JsonObject delta) =>
            E("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = delta });

        JsonObject message = JsonNode.Parse(reply.Message.GetRawText())!.AsObject();
        string named = (string?)message["model"] is { Length: > 0 } m ? m : string.IsNullOrEmpty(model) ? ModelId : model;
        yield return E("message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = (string?)message["id"] ?? "msg_demo_" + Guid.NewGuid().ToString("N")[..12], ["type"] = "message", ["role"] = "assistant", ["model"] = named,
                ["content"] = new JsonArray(), ["stop_reason"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = reply.InputTokens, ["output_tokens"] = 1 },
            },
        });

        int index = 0;
        foreach (JsonObject block in (message["content"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string type = (string?)block["type"] ?? "text";
            JsonObject opened = type switch
            {
                "text" => new JsonObject { ["type"] = "text", ["text"] = "" },
                "thinking" => new JsonObject { ["type"] = "thinking", ["thinking"] = "" },
                "tool_use" => new JsonObject { ["type"] = "tool_use", ["id"] = block["id"]?.DeepClone(), ["name"] = block["name"]?.DeepClone(), ["input"] = new JsonObject() },
                _ => (JsonObject)block.DeepClone(),
            };
            yield return E("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = opened });

            if (type == "text")
            {
                foreach (JsonNode? citation in block["citations"] as JsonArray ?? [])
                {
                    yield return Delta(index, new JsonObject { ["type"] = "citations_delta", ["citation"] = citation?.DeepClone() });
                }
            }

            (string whole, string kind, string field, int size) = type switch
            {
                "text" => ((string?)block["text"] ?? "", "text_delta", "text", TextPiece),
                "thinking" => ((string?)block["thinking"] ?? "", "thinking_delta", "thinking", TextPiece),
                "tool_use" => ((block["input"] ?? new JsonObject()).ToJsonString(), "input_json_delta", "partial_json", InputPiece),
                _ => ("", "", "", 0),
            };
            for (int at = 0; kind.Length > 0 && at < whole.Length; at += size)
            {
                yield return Delta(index, new JsonObject { ["type"] = kind, [field] = whole.Substring(at, Math.Min(size, whole.Length - at)) });
            }

            if (type == "thinking" && (string?)block["signature"] is { } signature)
            {
                yield return Delta(index, new JsonObject { ["type"] = "signature_delta", ["signature"] = signature });
            }

            yield return E("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = index });
            index++;
        }

        yield return E("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = message["stop_reason"]?.DeepClone() ?? "end_turn", ["stop_sequence"] = null },
            ["usage"] = new JsonObject { ["output_tokens"] = reply.OutputTokens },
        });
        yield return E("message_stop", new JsonObject { ["type"] = "message_stop" });
    }
}
