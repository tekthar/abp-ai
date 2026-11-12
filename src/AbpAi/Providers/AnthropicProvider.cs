using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace AbpAi.Providers;

/// <summary>
/// Anthropic's Messages API, which is the gateway's own shape: the request goes as it is (system blocks as they are, and
/// the call's provider options merged in), and the reply and stream events come back as they are. Settings:
/// <c>anthropic-version</c> (default 2026-10-21), <c>anthropic-beta</c>, and <c>auth</c> = <c>bearer</c> for a gateway
/// that takes the key as a bearer token. A call that may be cached (<see cref="AiRequest.Cache"/>) gets breakpoints on
/// its last system block and its last message's last block, unless it marked its own. A busy or briefly failing
/// provider is tried again (<see cref="AiOptions.ProviderRetries"/>) before a stream begins.
/// </summary>
public sealed class AnthropicProvider(IHttpClientFactory httpClients) : IAiProvider
{
    public const string DefaultEndpoint = "https://api.anthropic.com";
    public const string DefaultVersion = "2026-10-21";

    private readonly int _retries = new AiOptions().ProviderRetries;

    public AnthropicProvider(IHttpClientFactory httpClients, IOptions<AiOptions> options) : this(httpClients) => _retries = options.Value.ProviderRetries;

    public string Kind => "anthropic";

    public async Task<AiProviderReply> CompleteAsync(AiProviderCall call, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await ProviderHttp.SendAsync(Client, () => Request(call, stream: false), HttpCompletionOption.ResponseContentRead,
            call.Retries ?? _retries, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw ProviderHttp.Failed("Anthropic", (int)response.StatusCode, body, call.Target.ApiKey);
        }

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement message = doc.RootElement.Clone();
        Tokens tokens = TokensOf(message);
        return new AiProviderReply(message, tokens.Input, tokens.Output) { CacheWriteTokens = tokens.CacheWrite, CacheReadTokens = tokens.CacheRead };
    }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiProviderCall call, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await ProviderHttp.SendAsync(Client, () => Request(call, stream: true), HttpCompletionOption.ResponseHeadersRead,
            call.Retries ?? _retries, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw ProviderHttp.Failed("Anthropic", (int)response.StatusCode, body, call.Target.ApiKey);
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach ((string? name, string data) in Sse.ReadAsync(stream, cancellationToken))
        {
            JsonElement json;
            using (JsonDocument doc = JsonDocument.Parse(data))
            {
                json = doc.RootElement.Clone();
            }

            string type = json.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? name ?? "" : name ?? "";
            if (type == "ping")
            {
                continue;
            }

            if (type == "error")
            {
                string reason = ProviderHttp.Reason(data, call.Target.ApiKey);
                throw new AiProviderException($"Anthropic's stream failed: {reason}") { Reason = reason };
            }

            yield return new AiStreamEvent(type, json);
            if (type == "message_stop")
            {
                yield break;
            }
        }
    }

    /// <summary>The models the connection may call (<c>GET /v1/models</c>), every page of them.</summary>
    public async Task<IReadOnlyList<string>?> ListModelsAsync(AiTarget target, CancellationToken cancellationToken)
    {
        List<string> ids = [];
        string? after = null;
        for (int page = 0; page < 20; page++)
        {
            string address = Address(target, "models") + "?limit=1000" + (after is null ? "" : "&after_id=" + Uri.EscapeDataString(after));
            using HttpResponseMessage response = await ProviderHttp.SendAsync(Client, () =>
            {
                HttpRequestMessage request = new(HttpMethod.Get, address);
                Authorize(request, target);
                return request;
            }, HttpCompletionOption.ResponseContentRead, _retries, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw ProviderHttp.Failed("Anthropic", (int)response.StatusCode, body, target.ApiKey);
            }

            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
            {
                ids.AddRange(data.EnumerateArray().Select(m => m.TryGetProperty("id", out JsonElement id) ? id.GetString() : null).OfType<string>());
            }

            if (!(root.TryGetProperty("has_more", out JsonElement more) && more.ValueKind == JsonValueKind.True
                && root.TryGetProperty("last_id", out JsonElement last) && last.GetString() is { Length: > 0 } lastId))
            {
                break;
            }

            after = lastId;
        }

        return ids;
    }

    private HttpClient Client => httpClients.CreateClient(nameof(AnthropicProvider));

    internal static JsonObject Body(AiProviderCall call, bool stream)
    {
        JsonObject body = new()
        {
            ["model"] = call.Target.Model,
            ["max_tokens"] = call.MaxTokens,
            ["messages"] = JsonNode.Parse(call.Messages.GetRawText()),
        };
        if (call.SystemBlocks is { ValueKind: JsonValueKind.Array } blocks)
        {
            if (blocks.GetArrayLength() > 0)
            {
                body["system"] = JsonNode.Parse(blocks.GetRawText());
            }
        }
        else if (call.SystemText is { Length: > 0 } system)
        {
            body["system"] = system;
        }

        if (call.Tools is { ValueKind: JsonValueKind.Array } tools && tools.GetArrayLength() > 0)
        {
            body["tools"] = JsonNode.Parse(tools.GetRawText());
        }

        if (call.OutputSchema is { } schema)
        {
            body["output_config"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = JsonNode.Parse(schema.GetRawText()) } };
        }

        if (stream)
        {
            body["stream"] = true;
        }

        if (call.Cache || call.Request?.Cache == true)
        {
            MarkCache(body);
        }

        ProviderOptions.MergeInto(body, call.ProviderOptions, Reserved);
        return body;
    }

    /// <summary>
    /// Breakpoints for the provider's cache: on the last system block and on the last message's last block that may carry
    /// one, so the next call that starts the same reads its tools, system prompt and messages from the cache. A call that
    /// marked any <c>cache_control</c> itself is left as it is.
    /// </summary>
    internal static void MarkCache(JsonObject body)
    {
        if (body.ToJsonString().Contains("\"cache_control\"", StringComparison.Ordinal))
        {
            return;
        }

        JsonObject Ephemeral() => new() { ["type"] = "ephemeral" };
        if (body["system"] is JsonValue text && text.TryGetValue(out string? system) && system.Length > 0)
        {
            body["system"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = system, ["cache_control"] = Ephemeral() });
        }
        else if (body["system"] is JsonArray { Count: > 0 } systemBlocks && systemBlocks[^1] is JsonObject lastSystem)
        {
            lastSystem["cache_control"] = Ephemeral();
        }

        if (body["messages"] is JsonArray { Count: > 0 } messages && messages[^1] is JsonObject last)
        {
            if (last["content"] is JsonValue content && content.TryGetValue(out string? words))
            {
                last["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = words, ["cache_control"] = Ephemeral() });
            }
            else if (last["content"] is JsonArray parts
                && parts.OfType<JsonObject>().LastOrDefault(b => (string?)b["type"] is not ("thinking" or "redacted_thinking")) is { } block)
            {
                block["cache_control"] = Ephemeral();
            }
        }
    }

    /// <summary>The keys the gateway sets itself, never taken from a call's provider options.</summary>
    private static readonly string[] Reserved = ["model", "messages", "max_tokens", "system", "tools", "stream"];

    private static HttpRequestMessage Request(AiProviderCall call, bool stream)
    {
        HttpRequestMessage request = new(HttpMethod.Post, Address(call.Target, "messages"))
        {
            Content = new StringContent(Body(call, stream).ToJsonString(), Encoding.UTF8, "application/json"),
        };
        Authorize(request, call.Target);
        if (stream)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }

        return request;
    }

    /// <summary>An address of the API: the endpoint (or Anthropic's) up to <c>/v1</c>, then the path.</summary>
    private static string Address(AiTarget target, string path)
    {
        string endpoint = string.IsNullOrWhiteSpace(target.Endpoint) ? DefaultEndpoint : target.Endpoint.TrimEnd('/');
        return endpoint.EndsWith("/v1", StringComparison.Ordinal) ? $"{endpoint}/{path}" : $"{endpoint}/v1/{path}";
    }

    /// <summary>The version, the betas, and the key: as <c>x-api-key</c>, or a bearer token when the connection says <c>auth</c> = <c>bearer</c>.</summary>
    private static void Authorize(HttpRequestMessage request, AiTarget target)
    {
        IReadOnlyDictionary<string, string> settings = target.Settings;
        request.Headers.Add("anthropic-version", settings.TryGetValue("anthropic-version", out string? version) ? version : DefaultVersion);
        if (settings.TryGetValue("anthropic-beta", out string? beta) && beta.Length > 0)
        {
            request.Headers.Add("anthropic-beta", beta);
        }

        if (!string.IsNullOrEmpty(target.ApiKey))
        {
            if (settings.TryGetValue("auth", out string? auth) && auth == "bearer")
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.ApiKey);
            }
            else
            {
                request.Headers.Add("x-api-key", target.ApiKey);
            }
        }
    }

    private readonly record struct Tokens(int Input, int Output, int CacheWrite, int CacheRead);

    private static Tokens TokensOf(JsonElement message)
    {
        if (!message.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        static int Read(JsonElement usage, string name) => usage.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        return new Tokens(Read(usage, "input_tokens"), Read(usage, "output_tokens"), Read(usage, "cache_creation_input_tokens"), Read(usage, "cache_read_input_tokens"));
    }

    internal static string Excerpt(string body) => body.Length > 500 ? body[..500] : body;
}
