using System.Text.Json;

namespace AbpAi;

/// <summary>Where a call goes: a connection's endpoint, key and settings, and the model.</summary>
/// <param name="Settings">The connection's own settings (<c>anthropic-version</c>, <c>api-version</c> for Azure, headers).</param>
public sealed record AiTarget(Guid ConnectionId, string Connection, string Endpoint, string? ApiKey, string Model, IReadOnlyDictionary<string, string> Settings);

/// <summary>A call as a provider gets it: the target and the request in the Messages API's shape.</summary>
public sealed record AiProviderCall(AiTarget Target, JsonElement Messages, string? System, JsonElement? Tools, int MaxTokens, JsonElement? OutputSchema)
{
    /// <summary>What asked, and where: for the demo model's responders.</summary>
    public string Purpose { get; init; } = "";

    public Guid? ScopeId { get; init; }

    /// <summary>The system prompt as the Messages API's blocks, when the call gave them; they win over <see cref="System"/>.</summary>
    public JsonElement? SystemBlocks { get; init; }

    /// <summary>More of the provider's request (<see cref="AiRequest.ProviderOptions"/>): a JSON object, or null.</summary>
    public JsonElement? ProviderOptions { get; init; }

    /// <summary>
    /// The call as it was asked, with its <c>MaxTokens</c> after the limits that cap a reply: its purpose, account, scope,
    /// what it's about and its tools, for the demo model's responders and a host's own providers. Null for a call made by
    /// hand.
    /// </summary>
    public AiRequest? Request { get; init; }

    /// <summary>The system text: the blocks' text, joined, when there are blocks; else <see cref="System"/>.</summary>
    public string? SystemText => AiMessages.SystemText(System, SystemBlocks);
}

/// <summary>A provider's reply: the assistant message in the Messages API's shape, and the tokens it counted.</summary>
public sealed record AiProviderReply(JsonElement Message, int InputTokens, int OutputTokens);

/// <summary>A provider failed (its status, its words): the gateway refuses with <see cref="AiRefusalCodes.Provider"/>.</summary>
public sealed class AiProviderException(string message, int? status = null, Exception? inner = null) : Exception(message, inner)
{
    public int? Status { get; } = status;
}

/// <summary>
/// A kind of provider. AbpAi ships <c>anthropic</c>, <c>openai</c> (OpenAI, Azure OpenAI, Ollama, vLLM and most
/// gateways) and <c>demo</c>; others plug in by registering one. A provider translates to and from the Messages API's
/// shape; the gateway does the rest.
/// </summary>
public interface IAiProvider
{
    /// <summary>The kind a connection names: <c>anthropic</c>, <c>openai</c>, <c>demo</c>.</summary>
    string Kind { get; }

    Task<AiProviderReply> CompleteAsync(AiProviderCall call, CancellationToken cancellationToken);

    /// <summary>
    /// The reply as it's written, in the Messages API's events. The last event must be <c>message_stop</c>; the
    /// <c>message_delta</c> before it carries <c>usage.output_tokens</c>, and <c>message_start</c> <c>usage.input_tokens</c>.
    /// </summary>
    IAsyncEnumerable<AiStreamEvent> StreamAsync(AiProviderCall call, CancellationToken cancellationToken);

    /// <summary>
    /// The models the provider offers through a connection (its address, key and settings in the target), by their ids;
    /// null when it can't list them, so an admin sets them by hand.
    /// </summary>
    Task<IReadOnlyList<string>?> ListModelsAsync(AiTarget target, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>?>(null);
}
