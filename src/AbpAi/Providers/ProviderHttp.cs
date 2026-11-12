using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AbpAi.Providers;

/// <summary>
/// What the built-in providers share over HTTP: sending a call, again when the provider is busy or failed for a moment,
/// and reading the provider's own reason out of an error, with no key in it.
/// </summary>
public static partial class ProviderHttp
{
    /// <summary>The answers a call is tried again on: too many requests, a server's error, a gateway's, overloaded.</summary>
    public static readonly IReadOnlyList<int> Transient = [429, 500, 502, 503, 504, 529];

    /// <summary>The longest a provider's <c>retry-after</c> is waited for before trying again.</summary>
    public static readonly TimeSpan MostWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sends a request made anew for each try, trying again up to <paramref name="retries"/> times on a transient answer
    /// or when the provider couldn't be reached, after its <c>retry-after</c> (at most <see cref="MostWait"/>) or a short
    /// wait that doubles. The last answer is returned as it is, whatever it is; the caller reads its status.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, Func<HttpRequestMessage> request, HttpCompletionOption completion, int retries,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            HttpRequestMessage message = request();
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(message, completion, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < retries && !cancellationToken.IsCancellationRequested)
            {
                message.Dispose();
                await Task.Delay(Backoff(attempt, null), cancellationToken);
                continue;
            }

            if (attempt < retries && Transient.Contains((int)response.StatusCode))
            {
                TimeSpan wait = Backoff(attempt, response.Headers.RetryAfter);
                response.Dispose();
                message.Dispose();
                await Task.Delay(wait, cancellationToken);
                continue;
            }

            return response;
        }
    }

    /// <summary>How long to wait before a try: the provider's <c>retry-after</c>, else half a second, doubling each time, with a little jitter.</summary>
    internal static TimeSpan Backoff(int attempt, RetryConditionHeaderValue? retryAfter)
    {
        TimeSpan? asked = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (asked is { } wait && wait > TimeSpan.Zero)
        {
            return wait < MostWait ? wait : MostWait;
        }

        double ms = 500 * Math.Pow(2, attempt);
        return TimeSpan.FromMilliseconds(ms + Random.Shared.Next(0, (int)(ms / 4) + 1));
    }

    /// <summary>
    /// The provider's own reason in an error's body: its <c>error.message</c> (Anthropic's, OpenAI's, Azure's), an
    /// <c>error</c> that's text (Ollama's), or a <c>message</c>; else the body's start. Never the key: the connection's
    /// own, and anything that looks like one, read <c>[key]</c>.
    /// </summary>
    public static string Reason(string body, string? apiKey)
    {
        string reason = body;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out JsonElement error))
                {
                    if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String)
                    {
                        reason = message.GetString() ?? body;
                    }
                    else if (error.ValueKind == JsonValueKind.String)
                    {
                        reason = error.GetString() ?? body;
                    }
                }
                else if (root.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String)
                {
                    reason = message.GetString() ?? body;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON (a proxy's page): its start says what it can.
        }

        reason = reason.Trim();
        return Masked(reason.Length == 0 ? "it gave no reason" : reason.Length > 500 ? reason[..500] : reason, apiKey);
    }

    /// <summary>A text with the key, and anything that looks like a key, read <c>[key]</c>.</summary>
    public static string Masked(string text, string? apiKey)
    {
        if (apiKey is { Length: >= 4 })
        {
            text = text.Replace(apiKey, "[key]", StringComparison.Ordinal);
        }

        return KeyLike().Replace(text, "[key]");
    }

    /// <summary>An error from an answer that wasn't a success: the provider's words, its status and its reason.</summary>
    public static AiProviderException Failed(string who, int status, string body, string? apiKey)
    {
        string reason = Reason(body, apiKey);
        return new AiProviderException($"{who} answered {status}: {reason}", status) { Reason = reason };
    }

    /// <summary>Keys as providers write them, whole or partly hidden: <c>sk-…</c>, <c>sk-ant-…</c>, <c>sk-proj-****abcd</c>, and the like.</summary>
    [GeneratedRegex(@"\b(?:sk|rk|pk)-[A-Za-z0-9_\-\*]{6,}", RegexOptions.CultureInvariant)]
    private static partial Regex KeyLike();
}
