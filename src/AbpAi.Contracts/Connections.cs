namespace AbpAi;

/// <summary>A price per million tokens.</summary>
public sealed record AiPrice(decimal Input, decimal Output);

/// <summary>
/// What one of a connection's models can do, and whether it may be called: an admin's settings for it, by the provider's
/// id. A model with no settings is on, takes tools and images, and follows a schema when its connection does.
/// </summary>
public sealed record AiModelSettings
{
    /// <summary>What aliases and calls may call it by; its id when null.</summary>
    public string? Name { get; init; }

    /// <summary>It may be called. A model found on a connection starts off.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>It takes tools: a call with tools goes only to a model that does.</summary>
    public bool Tools { get; init; } = true;

    /// <summary>It takes images: a call with an image block goes only to a model that does.</summary>
    public bool Images { get; init; }

    /// <summary>It follows a JSON schema for a reply; null: as its connection says.</summary>
    public bool? StructuredOutput { get; init; }

    /// <summary>The most tokens its context holds, for people to read; null when unknown.</summary>
    public int? ContextTokens { get; init; }

    /// <summary>When its prices were last set; the admin sets it when a model's price changes.</summary>
    public DateTime? PriceSetAt { get; init; }
}

/// <summary>
/// A connection to a provider as the gateway reads it, from wherever it's kept (<see cref="IAiConnectionSource"/>):
/// everything the router needs, and never the key. For the site (no scope) or for a scope; it serves the scopes under
/// it too.
/// </summary>
/// <param name="Provider">The provider's kind (<c>anthropic</c>, <c>openai</c>, <c>demo</c>, or a host's own, such as <c>acme:bedrock</c>).</param>
public sealed record AiConnectionDescriptor(Guid Id, Guid? ScopeId, string Name, string Provider)
{
    private static readonly IReadOnlyDictionary<string, string> NoSettings = new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<string, AiPrice> NoPrices = new Dictionary<string, AiPrice>();
    private static readonly IReadOnlyDictionary<string, AiModelSettings> NoModelSettings = new Dictionary<string, AiModelSettings>();

    /// <summary>The provider's address; empty for its usual one.</summary>
    public string Endpoint { get; init; } = "";

    /// <summary>The provider's own settings (<c>anthropic-version</c>, <c>api-version</c>, <c>auth</c>).</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = NoSettings;

    /// <summary>The models it offers, by the provider's ids; the first that's on is its default.</summary>
    public IReadOnlyList<string> Models { get; init; } = [];

    /// <summary>Its models' settings, by their ids: a model with none is on and takes everything.</summary>
    public IReadOnlyDictionary<string, AiModelSettings> ModelSettings { get; init; } = NoModelSettings;

    /// <summary>The data classes it may receive, exactly.</summary>
    public IReadOnlyList<string> DataClasses { get; init; } = [];

    /// <summary>Where it runs, for people to read: <c>on-premises</c>, <c>eu</c>, <c>provider-cloud</c>.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Price per million input tokens, for its models unless <see cref="ModelPrices"/> names one.</summary>
    public decimal InputPrice { get; init; }

    /// <summary>Price per million output tokens.</summary>
    public decimal OutputPrice { get; init; }

    /// <summary>A model's own prices, by its id.</summary>
    public IReadOnlyDictionary<string, AiPrice> ModelPrices { get; init; } = NoPrices;

    /// <summary>Its models follow a JSON schema for a reply, unless a model's own settings say otherwise.</summary>
    public bool StructuredOutput { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>It has a key (for an admin to read; the key itself comes from <see cref="IAiConnectionSource.KeyAsync"/>).</summary>
    public bool HasKey { get; init; }

    public AiPrice PriceOf(string model) => ModelPrices.TryGetValue(model, out AiPrice? price) ? price : new AiPrice(InputPrice, OutputPrice);

    /// <summary>A model's settings, by its id; null when it has none.</summary>
    public AiModelSettings? SettingsOf(string model) => ModelSettings.TryGetValue(model, out AiModelSettings? settings) ? settings : null;

    /// <summary>What aliases and calls call a model by: its settings' name, else its id.</summary>
    public string NameOf(string model) => SettingsOf(model)?.Name is { Length: > 0 } name ? name : model;

    /// <summary>The id of a model it offers, by the id or by its name; null when it offers none by that.</summary>
    public string? ModelIdOf(string idOrName) => Models.Contains(idOrName, StringComparer.Ordinal)
        ? idOrName
        : Models.FirstOrDefault(m => string.Equals(NameOf(m), idOrName, StringComparison.Ordinal));
}

/// <summary>
/// Where the gateway's connections are kept. The default reads AbpAi's own <c>connections</c> table and decrypts its keys
/// with the site's string encryption. A host that keeps connections of its own (a product with a connections page)
/// gives its own, and the gateway's aliases, limits and usage refer to its connections by their ids; AbpAi's admin then
/// lists the host's connections and leaves setting them to the host.
/// </summary>
public interface IAiConnectionSource
{
    /// <summary>The site's connections and those set on the scopes named (a call's scope and those above it), on or off.</summary>
    Task<IReadOnlyList<AiConnectionDescriptor>> ListAsync(IReadOnlyCollection<Guid> scopeIds, CancellationToken cancellationToken);

    /// <summary>A connection's key, or null when it has none: asked only as a call goes out through it, never shown.</summary>
    Task<string?> KeyAsync(Guid connectionId, CancellationToken cancellationToken);
}
