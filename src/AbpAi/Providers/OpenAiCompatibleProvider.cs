using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace AbpAi.Providers;

/// <summary>
/// The Chat Completions API: OpenAI, Azure OpenAI, Ollama, vLLM and most gateways. The Messages API's shape is
/// translated to it and back, tool calls and stream events too. The endpoint is the API's base
/// (<c>https://api.openai.com/v1</c>, <c>http://localhost:11434/v1</c>). Settings: <c>api-version</c> (Azure; added to
/// the address), <c>auth</c> = <c>api-key</c> (Azure's header) or <c>bearer</c> (the default), and
/// <c>max-tokens-field</c> (<c>max_completion_tokens</c> for models that want it). System blocks are joined into the
/// system message; the call's provider options go into the request (<see cref="Translated"/>).
/// </summary>
public sealed class OpenAiCompatibleProvider(IHttpClientFactory httpClients) : IAiProvider
{
    public const string DefaultEndpoint = "https://api.openai.com/v1";

    private readonly int _retries = new AiOptions().ProviderRetries;

    public OpenAiCompatibleProvider(IHttpClientFactory httpClients, IOptions<AiOptions> options) : this(httpClients) => _retries = options.Value.ProviderRetries;

    public string Kind => "openai";

    private HttpClient Client => httpClients.CreateClient(nameof(OpenAiCompatibleProvider));

    public async Task<AiProviderReply> CompleteAsync(AiProviderCall call, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await ProviderHttp.SendAsync(Client, () => Request(call, stream: false), HttpCompletionOption.ResponseContentRead,
            call.Retries ?? _retries, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw ProviderHttp.Failed("The provider", (int)response.StatusCode, body, call.Target.ApiKey);
        }

        using JsonDocument doc = JsonDocument.Parse(body);
        return FromCompletion(doc.RootElement);
    }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiProviderCall call, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await ProviderHttp.SendAsync(Client, () => Request(call, stream: true), HttpCompletionOption.ResponseHeadersRead,
            call.Retries ?? _retries, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw ProviderHttp.Failed("The provider", (int)response.StatusCode, body, call.Target.ApiKey);
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
            if (doc.RootElement.TryGetProperty("error", out _))
            {
                string reason = ProviderHttp.Reason(data, call.Target.ApiKey);
                throw new AiProviderException($"The provider's stream failed: {reason}") { Reason = reason };
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

    /// <summary>The models the server offers (<c>GET {endpoint}/models</c>).</summary>
    public async Task<IReadOnlyList<string>?> ListModelsAsync(AiTarget target, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await ProviderHttp.SendAsync(Client, () =>
        {
            HttpRequestMessage request = new(HttpMethod.Get, Address(target, "models"));
            Authorize(request, target);
            return request;
        }, HttpCompletionOption.ResponseContentRead, _retries, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw ProviderHttp.Failed("The provider", (int)response.StatusCode, body, target.ApiKey);
        }

        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array
            ? [.. data.EnumerateArray().Select(m => m.TryGetProperty("id", out JsonElement id) ? id.GetString() : null).OfType<string>()]
            : [];
    }

    private static HttpRequestMessage Request(AiProviderCall call, bool stream)
    {
        HttpRequestMessage request = new(HttpMethod.Post, Address(call.Target, "chat/completions"))
        {
            Content = new StringContent(Body(call, stream).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        Authorize(request, call.Target);
        return request;
    }

    /// <summary>An address of the API: the endpoint (or OpenAI's), the path, and Azure's <c>api-version</c> when it's set.</summary>
    private static string Address(AiTarget target, string path)
    {
        string endpoint = (string.IsNullOrWhiteSpace(target.Endpoint) ? DefaultEndpoint : target.Endpoint).TrimEnd('/');
        return target.Settings.TryGetValue("api-version", out string? apiVersion) && apiVersion.Length > 0
            ? $"{endpoint}/{path}?api-version={Uri.EscapeDataString(apiVersion)}"
            : $"{endpoint}/{path}";
    }

    /// <summary>The key: a bearer token, or Azure's <c>api-key</c> header when the connection says <c>auth</c> = <c>api-key</c>.</summary>
    private static void Authorize(HttpRequestMessage request, AiTarget target)
    {
        if (string.IsNullOrEmpty(target.ApiKey))
        {
            return;
        }

        if (target.Settings.TryGetValue("auth", out string? auth) && auth == "api-key")
        {
            request.Headers.Add("api-key", target.ApiKey);
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.ApiKey);
        }
    }

    /// <summary>The Chat Completions request for a call in the Messages API's shape.</summary>
    internal static JsonObject Body(AiProviderCall call, bool stream)
    {
        JsonArray messages = [];
        if (call.SystemText is { Length: > 0 } system)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
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
                            ? c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : ResultText(c)
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
                    case "search_result":
                        // Chat Completions has no search results: each is its source, its title and its text, for the model to read.
                        parts.Add(new JsonObject { ["type"] = "text", ["text"] = SearchResultText(block) });
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

        ProviderOptions.MergeInto(body, Translated(call.ProviderOptions, body.ContainsKey("tools")), Reserved);
        return body;
    }

    /// <summary>The keys the gateway sets itself, never taken from a call's provider options.</summary>
    private static readonly string[] Reserved = ["model", "messages", "system", "tools", "stream", "stream_options", "max_tokens", "max_completion_tokens"];

    /// <summary>
    /// A call's provider options for Chat Completions: the Messages API's <c>tool_choice</c> in its shape (and only with
    /// tools), <c>stop_sequences</c> as <c>stop</c>, and <c>thinking</c> and <c>output_config</c>, which it has no key for,
    /// left out. The rest go as they are.
    /// </summary>
    internal static JsonElement? Translated(JsonElement? options, bool hasTools)
    {
        if (options is not { ValueKind: JsonValueKind.Object } given)
        {
            return null;
        }

        JsonObject translated = JsonNode.Parse(given.GetRawText())!.AsObject();
        translated.Remove("thinking");
        translated.Remove("output_config");
        if (translated["stop_sequences"] is { } stops)
        {
            translated.Remove("stop_sequences");
            if (!translated.ContainsKey("stop"))
            {
                translated["stop"] = stops;
            }
        }

        if (translated["tool_choice"] is { } choice)
        {
            translated.Remove("tool_choice");
            JsonNode? chat = choice is JsonObject messagesShape
                ? (string?)messagesShape["type"] switch
                {
                    "any" => "required",
                    "none" => "none",
                    "tool" => new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = (string?)messagesShape["name"] } },
                    "auto" => "auto",
                    _ => messagesShape.DeepClone(),
                }
                : choice;
            if (hasTools)
            {
                translated["tool_choice"] = chat;
            }
        }

        return JsonSerializer.SerializeToElement(translated);
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

        (int input, int output, int cached) = Usage(completion);
        reply["usage"] = new JsonObject { ["input_tokens"] = input, ["output_tokens"] = output, ["cache_read_input_tokens"] = cached };
        return new AiProviderReply(JsonSerializer.SerializeToElement(reply), input, output) { CacheReadTokens = cached };
    }

    internal static string StopReason(string? finish, bool toolUse) => finish switch
    {
        "tool_calls" or "function_call" => "tool_use",
        "length" => "max_tokens",
        "content_filter" => "refusal",
        _ => toolUse ? "tool_use" : "end_turn",
    };

    /// <summary>A search result as text: its source, its title, then its text blocks.</summary>
    internal static string SearchResultText(JsonElement block)
    {
        StringBuilder text = new();
        if (block.TryGetProperty("source", out JsonElement source) && source.ValueKind == JsonValueKind.String)
        {
            text.Append("Source: ").Append(source.GetString()).Append('\n');
        }

        if (block.TryGetProperty("title", out JsonElement title) && title.ValueKind == JsonValueKind.String)
        {
            text.Append("Title: ").Append(title.GetString()).Append('\n');
        }

        if (block.TryGetProperty("content", out JsonElement content))
        {
            text.Append(content.ValueKind == JsonValueKind.String ? content.GetString() : AiMessages.TextOf(JsonSerializer.SerializeToElement(new { content })));
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>A tool result's blocks as text: its text, and its search results as <see cref="SearchResultText"/> writes them.</summary>
    private static string ResultText(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
        {
            return AiMessages.TextOf(JsonSerializer.SerializeToElement(new { content }));
        }

        List<string> parts = [];
        foreach (JsonElement block in content.EnumerateArray())
        {
            string? type = block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;
            if (type == "text" && block.TryGetProperty("text", out JsonElement text))
            {
                parts.Add(text.GetString() ?? "");
            }
            else if (type == "search_result")
            {
                parts.Add(SearchResultText(block));
            }
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// The tokens a reply counted: its prompt's (less the ones read from the provider's cache, which it counts in them),
    /// its completion's, and the cached ones.
    /// </summary>
    private static (int Input, int Output, int Cached) Usage(JsonElement completion)
    {
        if (!completion.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return (0, 0, 0);
        }

        int prompt = usage.TryGetProperty("prompt_tokens", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
        int completed = usage.TryGetProperty("completion_tokens", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
        int cached = usage.TryGetProperty("prompt_tokens_details", out JsonElement details) && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("cached_tokens", out JsonElement k) && k.ValueKind == JsonValueKind.Number ? Math.Clamp(k.GetInt32(), 0, prompt) : 0;
        return (prompt - cached, completed, cached);
    }

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
        private int _cached;

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
                (_input, _output, _cached) = Usage(chunk);
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
                type = "message_delta", delta = new { stop_reason = StopReason(_finish, _toolUse) },
                usage = new { input_tokens = _input, output_tokens = _output, cache_read_input_tokens = _cached },
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
