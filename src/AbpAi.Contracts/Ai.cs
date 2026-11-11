using System.Text.Json;

namespace AbpAi;

/// <summary>
/// The model gateway: one call for every host, in the shape of the Messages API. An alias ("fast", "smart", "local")
/// leads to a model through the call's scope, then the site, else a model by its name. Every call is checked against
/// the data rules and the limits before it leaves, and recorded after.
/// </summary>
public interface IAi
{
    /// <summary>One whole reply.</summary>
    /// <exception cref="AiRefusedException">No model may answer, a limit is reached, the data may not go there, or the provider failed.</exception>
    Task<AiReply> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The reply as it's written: the Messages API's stream events (<c>message_start</c>, <c>content_block_start</c>,
    /// <c>content_block_delta</c>, <c>content_block_stop</c>, <c>message_delta</c>, <c>message_stop</c>), then one last
    /// event of type <see cref="AiStreamEvent.Done"/> carrying the whole <see cref="AiReply"/>. A refusal is thrown before
    /// the first event.
    /// </summary>
    IAsyncEnumerable<AiStreamEvent> StreamAsync(AiRequest request, CancellationToken cancellationToken = default);

    /// <summary>Which model an alias leads to for a scope and a data class, without calling it. Carries no key.</summary>
    /// <exception cref="AiRefusedException"><see cref="AiRefusalCodes.NoModel"/> or <see cref="AiRefusalCodes.DataNotAllowed"/>.</exception>
    Task<AiChoice> ResolveAsync(string alias, Guid? scopeId = null, string? dataClass = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// A call to a model.
/// </summary>
/// <param name="Alias">An alias, or a model by its name.</param>
/// <param name="Messages">The conversation: a JSON array of <c>{ role, content }</c> in the Messages API's shape.</param>
public sealed record AiRequest(string Alias, JsonElement Messages)
{
    /// <summary>The system text.</summary>
    public string? System { get; init; }

    /// <summary>
    /// The system prompt as the Messages API's blocks (<c>[{ type: "text", text, cache_control }]</c>), for a call that marks
    /// what a provider may cache; a string is taken as the text. When set it wins over <see cref="System"/>.
    /// </summary>
    public JsonElement? SystemBlocks { get; init; }

    /// <summary>
    /// More of the provider's request, as a JSON object: keys the gateway doesn't set itself (<c>thinking</c>,
    /// <c>temperature</c>, <c>tool_choice</c>) go into the request body as they are. The gateway's own (the model, the
    /// messages, the system prompt, the tools, the most tokens, streaming) are never taken from here.
    /// </summary>
    public JsonElement? ProviderOptions { get; init; }

    /// <summary>The tools: <c>[{ name, description, input_schema }]</c>, or null.</summary>
    public JsonElement? Tools { get; init; }

    public int MaxTokens { get; init; } = 1024;

    /// <summary>A JSON schema the reply must follow; only models that support it are chosen.</summary>
    public JsonElement? OutputSchema { get; init; }

    /// <summary>What asked, for usage: <c>skill:triage@3</c>, <c>agent:analyst</c>, <c>describe-model</c>.</summary>
    public string Purpose { get; init; } = "unknown";

    /// <summary>The host's scope the call is made in (a project, a model set, an org unit); null for the site.</summary>
    public Guid? ScopeId { get; init; }

    /// <summary>The account that calls: a person, or an agent's account. Its own limits and the per-person limits apply.</summary>
    public Guid? AccountId { get; init; }

    /// <summary>
    /// The person an account acts for (the person who started an agent's run): the per-person limits count against them
    /// too. Null when the account calls for itself.
    /// </summary>
    public Guid? OnBehalfOf { get; init; }

    /// <summary>The record the call was about, for usage.</summary>
    public AiSubject? About { get; init; }

    /// <summary>
    /// The highest class of data the call carries (<see cref="AiDataClasses"/>, or the site's own). Null counts as the
    /// highest the site defines: a call that says nothing may go only where everything may go.
    /// </summary>
    public string? DataClass { get; init; }

    /// <summary>A call with one user message.</summary>
    public static AiRequest Text(string alias, string prompt) => new(alias, AiMessages.User(prompt));
}

/// <summary>What a call was about: a kind (<c>record</c>, <c>model-set</c>) and the host's id.</summary>
public sealed record AiSubject(string Kind, string Id);

/// <summary>Tokens and cost of a call.</summary>
public sealed record AiUsage(int InputTokens, int OutputTokens, decimal Cost);

/// <summary>
/// A reply: the assistant message (<c>role</c>, <c>content</c> blocks, <c>stop_reason</c>), the model and connection that
/// answered, and what it cost.
/// </summary>
public sealed record AiReply(JsonElement Message, string Model, AiUsage Usage)
{
    /// <summary>The usage record's id.</summary>
    public Guid UsageId { get; init; }

    /// <summary>The connection's name.</summary>
    public string Connection { get; init; } = "";

    public string? StopReason => Message.TryGetProperty("stop_reason", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;

    /// <summary>The text blocks, joined.</summary>
    public string Text => AiMessages.TextOf(Message);

    /// <summary>The reply as JSON, when the call gave an <see cref="AiRequest.OutputSchema"/>.</summary>
    public JsonElement? Output => AiMessages.TryParseJson(Text);
}

/// <summary>A stream event: its type and data as the Messages API sends it, and on the last one the whole reply.</summary>
public sealed record AiStreamEvent(string Type, JsonElement Data)
{
    /// <summary>The type of the last event, which carries <see cref="Reply"/>.</summary>
    public const string Done = "abp_ai_done";

    public AiReply? Reply { get; init; }
}

/// <summary>
/// Where an alias leads: the model, the provider and connection (never its key), where the alias was set (<c>scope</c>,
/// <c>site</c>, or <c>model</c> when it was a model's name), what the model can do, and what the connection may receive.
/// </summary>
/// <param name="Model">The provider's id for the model.</param>
public sealed record AiChoice(string Alias, string Model, string Provider, Guid ConnectionId, string Connection, string Source, Guid? SetOnScopeId,
    bool StructuredOutput, IReadOnlyList<string> DataClasses, IReadOnlyList<string> Labels)
{
    /// <summary>What people and aliases call the model; its id unless an admin named it.</summary>
    public string ModelName { get; init; } = Model;

    /// <summary>The model takes tools.</summary>
    public bool Tools { get; init; } = true;

    /// <summary>The model takes images.</summary>
    public bool Images { get; init; } = true;
}
