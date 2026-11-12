namespace AbpAi;

/// <summary>
/// Texts to embed: vectors a host searches by meaning (an agent's memory, a document's passages). An alias leads to an
/// embedding model as it leads to any model, through the call's scope, then the site, else a model by its name; the
/// data rules, the limits and the ledger hold as for any call.
/// </summary>
/// <param name="Alias">An alias, or a model by its name.</param>
/// <param name="Inputs">The texts, one vector each, in order.</param>
public sealed record AiEmbedRequest(string Alias, IReadOnlyList<string> Inputs)
{
    /// <summary>The vectors' size, for a model that can shorten them (OpenAI's <c>text-embedding-3</c>); null: the model's own.</summary>
    public int? Dimensions { get; init; }

    /// <summary>
    /// What the texts are, for a provider that embeds them differently (<see cref="AiEmbedInputTypes"/>): what's searched
    /// for, or what's searched. Sent only to a connection whose <c>input-type</c> setting is <c>send</c>.
    /// </summary>
    public string? InputType { get; init; }

    /// <summary>What asked, for usage: <c>agent:ask:memory</c>, <c>search:passages</c>.</summary>
    public string Purpose { get; init; } = "unknown";

    /// <summary>The host's scope the call is made in; null for the site.</summary>
    public Guid? ScopeId { get; init; }

    /// <summary>The account that calls: a person, or an agent's account.</summary>
    public Guid? AccountId { get; init; }

    /// <summary>The person the account acts for, whose per-person limits count it too.</summary>
    public Guid? OnBehalfOf { get; init; }

    /// <summary>What the texts are about, for usage (an episode of an agent's memory).</summary>
    public AiSubject? About { get; init; }

    /// <summary>The highest class of data the texts carry; null counts as the highest the site defines.</summary>
    public string? DataClass { get; init; }
}

/// <summary>What texts to embed are, for a provider that embeds them differently.</summary>
public static class AiEmbedInputTypes
{
    /// <summary>What's searched for: a question.</summary>
    public const string Query = "query";

    /// <summary>What's searched: a passage, a fact, an episode.</summary>
    public const string Document = "document";
}

/// <summary>The vectors, one for each text in order, the model and connection that made them, and what they cost.</summary>
public sealed record AiEmbedding(IReadOnlyList<float[]> Vectors, string Model, AiUsage Usage)
{
    /// <summary>The usage record's id.</summary>
    public Guid UsageId { get; init; }

    /// <summary>The connection's name.</summary>
    public string Connection { get; init; } = "";

    /// <summary>The vectors' size.</summary>
    public int Dimensions => Vectors.Count == 0 ? 0 : Vectors[0].Length;
}

/// <summary>An embedding call as a provider gets it: the target, the texts, and how.</summary>
public sealed record AiEmbedCall(AiTarget Target, IReadOnlyList<string> Inputs)
{
    public int? Dimensions { get; init; }

    public string? InputType { get; init; }

    public string Purpose { get; init; } = "";

    public Guid? ScopeId { get; init; }

    /// <summary>How many times a built-in provider tries again when the provider is busy; null: the site's.</summary>
    public int? Retries { get; init; }

    /// <summary>The call as it was asked; null for a call made by hand.</summary>
    public AiEmbedRequest? Request { get; init; }
}

/// <summary>A provider's vectors, one for each text in order, and the tokens it counted.</summary>
public sealed record AiProviderEmbedding(IReadOnlyList<float[]> Vectors, int InputTokens);
