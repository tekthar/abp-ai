using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AbpAi.Providers;

/// <summary>
/// Anthropic's Messages API, which is the gateway's own shape: the request goes as it is (system blocks as they are, and
/// the call's provider options merged in), and the reply and stream events come back as they are. Settings:
/// <c>anthropic-version</c> (default 2026-10-21), <c>anthropic-beta</c>, and <c>auth</c> = <c>bearer</c> for a gateway
/// that takes the key as a bearer token.
/// </summary>
public sealed class AnthropicProvider(IHttpClientFactory httpClients) : IAiProvider
{
    public const string DefaultEndpoint = "https://api.anthropic.com";
    public const string DefaultVersion = "2026-10-21";

    public string Kind => "anthropic";

    public async Task<AiProviderReply> CompleteAsync(AiProviderCall call, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = Request(call, stream: false);
        using HttpResponseMessage response = await httpClients.CreateClient(nameof(AnthropicProvider)).SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new AiProviderException($"Anthropic answered {(int)response.StatusCode}: {Excerpt(body)}", (int)response.StatusCode);
        }

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement message = doc.RootElement.Clone();
        (int input, int output) = Tokens(message);
        return new AiProviderReply(message, input, output);
    }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiProviderCall call, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = Request(call, stream: true);
        using HttpResponseMessage response = await httpClients.CreateClient(nameof(AnthropicProvider))
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new AiProviderException($"Anthropic answered {(int)response.StatusCode}: {Excerpt(body)}", (int)response.StatusCode);
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
                throw new AiProviderException($"Anthropic's stream failed: {Excerpt(data)}");
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
            using HttpRequestMessage request = new(HttpMethod.Get, Address(target, "models") + "?limit=1000" + (after is null ? "" : "&after_id=" + Uri.EscapeDataString(after)));
            Authorize(request, target);
            using HttpResponseMessage response = await httpClients.CreateClient(nameof(AnthropicProvider)).SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new AiProviderException($"Anthropic answered {(int)response.StatusCode}: {Excerpt(body)}", (int)response.StatusCode);
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

        ProviderOptions.MergeInto(body, call.ProviderOptions, Reserved);
        return body;
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

    private static (int Input, int Output) Tokens(JsonElement message) =>
        message.TryGetProperty("usage", out JsonElement usage)
            ? (usage.TryGetProperty("input_tokens", out JsonElement i) ? i.GetInt32() : 0, usage.TryGetProperty("output_tokens", out JsonElement o) ? o.GetInt32() : 0)
            : (0, 0);

    internal static string Excerpt(string body) => body.Length > 500 ? body[..500] : body;
}
