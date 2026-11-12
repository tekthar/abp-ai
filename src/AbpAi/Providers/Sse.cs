using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AbpAi.Providers;

/// <summary>Reads server-sent events: each record's event name and data, as a stream sends them.</summary>
internal static class Sse
{
    public static async IAsyncEnumerable<(string? Event, string Data)> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using StreamReader reader = new(stream, Encoding.UTF8);
        string? name = null;
        StringBuilder data = new();
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return (name, data.ToString());
                }

                name = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                name = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line[5..].TrimStart());
            }
        }

        if (data.Length > 0)
        {
            yield return (name, data.ToString());
        }
    }
}

/// <summary>
/// Builds the whole assistant message from the Messages API's stream events, and the tokens they counted: the reply a
/// stream ends with.
/// </summary>
internal sealed class AiMessageBuilder
{
    private readonly JsonObject _message = new() { ["role"] = "assistant", ["content"] = new JsonArray() };
    private readonly Dictionary<int, StringBuilder> _json = [];

    public int InputTokens { get; private set; }

    public int OutputTokens { get; private set; }

    public int CacheWriteTokens { get; private set; }

    public int CacheReadTokens { get; private set; }

    public void Add(AiStreamEvent e)
    {
        JsonElement data = e.Data;
        switch (e.Type)
        {
            case "message_start" when data.TryGetProperty("message", out JsonElement message):
                foreach (string key in new[] { "id", "model" })
                {
                    if (message.TryGetProperty(key, out JsonElement v))
                    {
                        _message[key] = JsonNode.Parse(v.GetRawText());
                    }
                }

                Usage(message);
                break;
            case "content_block_start" when data.TryGetProperty("content_block", out JsonElement block):
                Content.Add(JsonNode.Parse(block.GetRawText()));
                break;
            case "content_block_delta" when data.TryGetProperty("delta", out JsonElement delta) && Block(data) is { } target:
                string type = delta.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";
                switch (type)
                {
                    case "text_delta":
                        target["text"] = (target["text"]?.GetValue<string>() ?? "") + delta.GetProperty("text").GetString();
                        break;
                    case "thinking_delta":
                        target["thinking"] = (target["thinking"]?.GetValue<string>() ?? "") + delta.GetProperty("thinking").GetString();
                        break;
                    case "signature_delta":
                        target["signature"] = delta.GetProperty("signature").GetString();
                        break;
                    case "citations_delta" when delta.TryGetProperty("citation", out JsonElement citation):
                        if (target["citations"] is not JsonArray citations)
                        {
                            target["citations"] = citations = [];
                        }

                        citations.Add(JsonNode.Parse(citation.GetRawText()));
                        break;
                    case "input_json_delta":
                        int index = data.GetProperty("index").GetInt32();
                        if (!_json.TryGetValue(index, out StringBuilder? json))
                        {
                            _json[index] = json = new StringBuilder();
                        }

                        json.Append(delta.GetProperty("partial_json").GetString());
                        break;
                }

                break;
            case "content_block_stop" when Block(data) is { } done:
                int at = data.GetProperty("index").GetInt32();
                if (_json.Remove(at, out StringBuilder? input))
                {
                    done["input"] = input.Length == 0 ? new JsonObject() : ParseOrRaw(input.ToString());
                }

                break;
            case "message_delta":
                if (data.TryGetProperty("delta", out JsonElement d) && d.TryGetProperty("stop_reason", out JsonElement stop))
                {
                    _message["stop_reason"] = JsonNode.Parse(stop.GetRawText());
                }

                Usage(data);
                break;
        }
    }

    public JsonElement Message => JsonSerializer.SerializeToElement(_message);

    private JsonArray Content => (JsonArray)_message["content"]!;

    private JsonObject? Block(JsonElement data) =>
        data.TryGetProperty("index", out JsonElement i) && i.GetInt32() is var index && index >= 0 && index < Content.Count ? Content[index] as JsonObject : null;

    private void Usage(JsonElement holder)
    {
        if (!holder.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (usage.TryGetProperty("input_tokens", out JsonElement input) && input.ValueKind == JsonValueKind.Number && input.GetInt32() > 0)
        {
            InputTokens = input.GetInt32();
        }

        if (usage.TryGetProperty("output_tokens", out JsonElement output) && output.ValueKind == JsonValueKind.Number)
        {
            OutputTokens = output.GetInt32();
        }

        if (usage.TryGetProperty("cache_creation_input_tokens", out JsonElement written) && written.ValueKind == JsonValueKind.Number && written.GetInt32() > 0)
        {
            CacheWriteTokens = written.GetInt32();
        }

        if (usage.TryGetProperty("cache_read_input_tokens", out JsonElement read) && read.ValueKind == JsonValueKind.Number && read.GetInt32() > 0)
        {
            CacheReadTokens = read.GetInt32();
        }
    }

    private static JsonNode ParseOrRaw(string json)
    {
        try
        {
            return JsonNode.Parse(json) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject { ["_raw"] = json };
        }
    }
}
