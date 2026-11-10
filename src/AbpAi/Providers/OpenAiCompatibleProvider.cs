using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AbpAi.Providers;

/// <summary>
/// The Chat Completions API: OpenAI, Azure OpenAI, Ollama, vLLM and most gateways. The Messages API's shape is
/// translated to it and back, tool calls and stream events too. The endpoint is the API's base
/// (<c>https://api.openai.com/v1</c>, <c>http://localhost:11434/v1</c>). Settings: <c>api-version</c> (Azure; added to
/// the address), <c>auth</c> = <c>api-key</c> (Azure's header) or <c>bearer</c> (the default), and
/// <c>max-tokens-field</c> (<c>max_completion_tokens</c> for models that want it).
/// </summary>
public sealed class OpenAiCompatibleProvider(IHttpClientFactory httpClients) : IAiProvider
{
    public const string DefaultEndpoint = "https://api.openai.com/v1";

    public string Kind => "openai";

    public async Task<AiProviderReply> CompleteAsync(AiProviderCall call, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = Request(call, stream: false);
        using HttpResponseMessage response = await httpClients.CreateClient(nameof(OpenAiCompatibleProvider)).SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AiProviderException($"The provider answered {(int)response.StatusCode}: {AnthropicProvider.Excerpt(body)}", (int)response.StatusCode);
        }

        using JsonDocument doc = JsonDocument.Parse(body);
        return FromCompletion(doc.RootElement);
    }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiProviderCall call, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = Request(call, stream: true);
        using HttpResponseMessage response = await httpClients.CreateClient(nameof(OpenAiCompatibleProvider))
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new AiProviderException($"The provider answered {(int)response.StatusCode}: {AnthropicProvider.Excerpt(body)}", (int)response.StatusCode);
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        StreamTranslator translator = new(call.Target.Model);
        await foreach ((_, string data) in Sse.ReadAsync(stream, cancellationToken))
        {
            if (data == "[DONE]")
            {
                break;
            }

            using JsonDocument doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("error", out JsonElement error))
            {
                throw new AiProviderException($"The provider's stream failed: {AnthropicProvider.Excerpt(error.GetRawText())}");
            }

            foreach (AiStreamEvent e in translator.Add(doc.RootElement))
            {
                yield return e;
            }
        }

        foreach (AiStreamEvent e in translator.End())
        {
            yield return e;
        }
    }

    private static HttpRequestMessage Request(AiProviderCall call, bool stream)
    {
        IReadOnlyDictionary<string, string> settings = call.Target.Settings;
        string endpoint = (string.IsNullOrWhiteSpace(call.Target.Endpoint) ? DefaultEndpoint : call.Target.Endpoint).TrimEnd('/');
        string url = endpoint + "/chat/completions";
        if (settings.TryGetValue("api-version", out string? apiVersion) && apiVersion.Length > 0)
        {
            url += "?api-version=" + Uri.EscapeDataString(apiVersion);
        }

        HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(Body(call, stream).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(call.Target.ApiKey))
        {
            if (settings.TryGetValue("auth", out string? auth) && auth == "api-key")
            {
                request.Headers.Add("api-key", call.Target.ApiKey);
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", call.Target.ApiKey);
            }
        }

        return request;
    }

    /// <summary>The Chat Completions request for a call in the Messages API's shape.</summary>
    internal static JsonObject Body(AiProviderCall call, bool stream)
    {
        JsonArray messages = [];
        if (!string.IsNullOrEmpty(call.System))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = call.System });
        }

        foreach (JsonElement message in call.Messages.EnumerateArray())
        {
            string role = message.GetProperty("role").GetString() ?? "user";
            JsonElement content = message.GetProperty("content");
            if (content.ValueKind == JsonValueKind.String)
            {
                messages.Add(new JsonObject { ["role"] = role, ["content"] = content.GetString() });
                continue;
            }

            if (role == "assistant")
            {
                StringBuilder text = new();
                JsonArray calls = [];
                foreach (JsonElement block in content.EnumerateArray())
                {
                    switch (block.GetProperty("type").GetString())
                    {
                        case "text":
                            text.Append(block.GetProperty("text").GetString());
                            break;
                        case "tool_use":
                            calls.Add(new JsonObject
                            {
                                ["id"] = block.GetProperty("id").GetString(),
                                ["type"] = "function",
                                ["function"] = new JsonObject
                                {
                                    ["name"] = block.GetProperty("name").GetString(),
                                    ["arguments"] = block.TryGetProperty("input", out JsonElement input) ? input.GetRawText() : "{}",
                                },
                            });
                            break;
                    }
                }

                JsonObject assistant = new() { ["role"] = "assistant", ["content"] = text.Length > 0 ? text.ToString() : null };
                if (calls.Count > 0)
                {
                    assistant["tool_calls"] = calls;
                }

                messages.Add(assistant);
                continue;
            }

            // A user's turn: its tool results first (each its own message, right after the assistant's calls), then the rest.
            JsonArray parts = [];
            foreach (JsonElement block in content.EnumerateArray())
            {
                switch (block.GetProperty("type").GetString())
                {
                    case "tool_result":
                        string result = block.TryGetProperty("content", out JsonElement c)
                            ? c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : AiMessages.TextOf(JsonSerializer.SerializeToElement(new { content = c }))
                            : "";
                        if (block.TryGetProperty("is_error", out JsonElement isError) && isError.ValueKind == JsonValueKind.True)
                        {
                            result = "Error: " + result;
                        }

                        messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = block.GetProperty("tool_use_id").GetString(), ["content"] = result });
                        break;
                    case "text":
                        parts.Add(new JsonObject { ["type"] = "text", ["text"] = block.GetProperty("text").GetString() });
                        break;
                    case "image" when block.TryGetProperty("source", out JsonElement source) && source.TryGetProperty("data", out JsonElement data):
                        string media = source.TryGetProperty("media_type", out JsonElement m) ? m.GetString() ?? "image/png" : "image/png";
                        parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{media};base64,{data.GetString()}" } });
                        break;
                }
            }

            if (parts.Count > 0)
            {
                messages.Add(new JsonObject
                {
                    ["role"] = role,
                    ["content"] = parts.All(p => p!["type"]!.GetValue<string>() == "text")
                        ? string.Join("\n", parts.Select(p => p!["text"]!.GetValue<string>()))
                        : parts,
                });
            }
        }

        string maxField = call.Target.Settings.TryGetValue("max-tokens-field", out string? field) && field.Length > 0 ? field : "max_tokens";
        JsonObject body = new() { ["model"] = call.Target.Model, ["messages"] = messages, [maxField] = call.MaxTokens };
        if (call.Tools is { ValueKind: JsonValueKind.Array } tools && tools.GetArrayLength() > 0)
        {
            JsonArray functions = [];
            foreach (JsonElement tool in tools.EnumerateArray())
            {
                functions.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.GetProperty("name").GetString(),
                        ["description"] = tool.TryGetProperty("description", out JsonElement d) ? d.GetString() : null,
                        ["parameters"] = tool.TryGetProperty("input_schema", out JsonElement s) ? JsonNode.Parse(s.GetRawText()) : new JsonObject { ["type"] = "object" },
                    },
                });
            }

            body["tools"] = functions;
        }

        if (call.OutputSchema is { } schema)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "reply", ["schema"] = JsonNode.Parse(schema.GetRawText()) },
            };
        }

        if (stream)
        {
            body["stream"] = true;
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        return body;
    }

    /// <summary>A Chat Completions reply in the Messages API's shape.</summary>
    internal static AiProviderReply FromCompletion(JsonElement completion)
    {
        JsonElement choice = completion.GetProperty("choices")[0];
        JsonElement message = choice.GetProperty("message");
        JsonArray content = [];
        if (message.TryGetProperty("content", out JsonElement text) && text.ValueKind == JsonValueKind.String && text.GetString() is { Length: > 0 } t)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = t });
        }

        if (message.TryGetProperty("tool_calls", out JsonElement calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement call in calls.EnumerateArray())
            {
                JsonElement function = call.GetProperty("function");
                content.Add(new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = call.TryGetProperty("id", out JsonElement id) ? id.GetString() : "call_" + Guid.NewGuid().ToString("N")[..16],
                    ["name"] = function.GetProperty("name").GetString(),
                    ["input"] = Arguments(function.TryGetProperty("arguments", out JsonElement a) ? a.GetString() : null),
                });
            }
        }

        string? finish = choice.TryGetProperty("finish_reason", out JsonElement f) ? f.GetString() : null;
        JsonObject reply = new()
        {
            ["role"] = "assistant",
            ["content"] = content,
            ["stop_reason"] = StopReason(finish, content.Any(b => b!["type"]!.GetValue<string>() == "tool_use")),
        };
        if (completion.TryGetProperty("model", out JsonElement model))
        {
            reply["model"] = model.GetString();
        }

        (int input, int output) = Usage(completion);
        reply["usage"] = new JsonObject { ["input_tokens"] = input, ["output_tokens"] = output };
        return new AiProviderReply(JsonSerializer.SerializeToElement(reply), input, output);
    }

    internal static string StopReason(string? finish, bool toolUse) => finish switch
    {
        "tool_calls" or "function_call" => "tool_use",
        "length" => "max_tokens",
        "content_filter" => "refusal",
        _ => toolUse ? "tool_use" : "end_turn",
    };

    private static (int Input, int Output) Usage(JsonElement completion) =>
        completion.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object
            ? (usage.TryGetProperty("prompt_tokens", out JsonElement p) ? p.GetInt32() : 0, usage.TryGetProperty("completion_tokens", out JsonElement c) ? c.GetInt32() : 0)
            : (0, 0);

    private static JsonNode Arguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(json) ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject { ["_raw"] = json };
        }
    }

    /// <summary>Turns Chat Completions chunks into the Messages API's stream events.</summary>
    internal sealed class StreamTranslator(string model)
    {
        private bool _started;
        private int _index = -1;
        private string? _open; // "text" or a tool call's index
        private string? _finish;
        private bool _toolUse;
        private int _input;
        private int _output;

        public IEnumerable<AiStreamEvent> Add(JsonElement chunk)
        {
            List<AiStreamEvent> events = [];
            if (!_started)
            {
                _started = true;
                events.Add(Event("message_start", new
                {
                    type = "message_start",
                    message = new { id = chunk.TryGetProperty("id", out JsonElement id) ? id.GetString() : null, type = "message", role = "assistant", model,
                        content = Array.Empty<object>(), usage = new { input_tokens = 0, output_tokens = 0 } },
                }));
            }

            if (chunk.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
            {
                (_input, _output) = Usage(chunk);
            }

            if (!chunk.TryGetProperty("choices", out JsonElement choices) || choices.GetArrayLength() == 0)
            {
                return events;
            }

            JsonElement choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out JsonElement f) && f.ValueKind == JsonValueKind.String)
            {
                _finish = f.GetString();
            }

            if (!choice.TryGetProperty("delta", out JsonElement delta))
            {
                return events;
            }

            if (delta.TryGetProperty("content", out JsonElement text) && text.ValueKind == JsonValueKind.String && text.GetString() is { Length: > 0 } piece)
            {
                if (_open != "text")
                {
                    Close(events);
                    _open = "text";
                    events.Add(Event("content_block_start", new { type = "content_block_start", index = ++_index, content_block = new { type = "text", text = "" } }));
                }

                events.Add(Event("content_block_delta", new { type = "content_block_delta", index = _index, delta = new { type = "text_delta", text = piece } }));
            }

            if (delta.TryGetProperty("tool_calls", out JsonElement calls) && calls.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement call in calls.EnumerateArray())
                {
                    string key = "tool:" + (call.TryGetProperty("index", out JsonElement i) ? i.GetInt32() : 0);
                    if (_open != key)
                    {
                        Close(events);
                        _open = key;
                        _toolUse = true;
                        JsonElement function = call.TryGetProperty("function", out JsonElement fn) ? fn : default;
                        events.Add(Event("content_block_start", new
                        {
                            type = "content_block_start", index = ++_index,
                            content_block = new
                            {
                                type = "tool_use", id = call.TryGetProperty("id", out JsonElement id) ? id.GetString() : "call_" + Guid.NewGuid().ToString("N")[..16],
                                name = function.ValueKind == JsonValueKind.Object && function.TryGetProperty("name", out JsonElement n) ? n.GetString() : "",
                                input = new { },
                            },
                        }));
                    }

                    if (call.TryGetProperty("function", out JsonElement f2) && f2.TryGetProperty("arguments", out JsonElement args) && args.GetString() is { Length: > 0 } part)
                    {
                        events.Add(Event("content_block_delta", new { type = "content_block_delta", index = _index, delta = new { type = "input_json_delta", partial_json = part } }));
                    }
                }
            }

            return events;
        }

        public IEnumerable<AiStreamEvent> End()
        {
            List<AiStreamEvent> events = [];
            if (!_started)
            {
                throw new AiProviderException("The provider's stream ended before it began.");
            }

            Close(events);
            events.Add(Event("message_delta", new
            {
                type = "message_delta", delta = new { stop_reason = StopReason(_finish, _toolUse) }, usage = new { input_tokens = _input, output_tokens = _output },
            }));
            events.Add(Event("message_stop", new { type = "message_stop" }));
            return events;
        }

        private void Close(List<AiStreamEvent> events)
        {
            if (_open is not null)
            {
                events.Add(Event("content_block_stop", new { type = "content_block_stop", index = _index }));
                _open = null;
            }
        }

        private static AiStreamEvent Event(string type, object data) => new(type, JsonSerializer.SerializeToElement(data));
    }
}
