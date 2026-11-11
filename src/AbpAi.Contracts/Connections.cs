namespace AbpAi;

/// <summary>A price per million tokens.</summary>
public sealed record AiPrice(decimal Input, decimal Output);

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

    /// <summary>The provider's address; empty for its usual one.</summary>
    public string Endpoint { get; init; } = "";

    /// <summary>The provider's own settings (<c>anthropic-version</c>, <c>api-version</c>, <c>auth</c>).</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = NoSettings;

    /// <summary>The models it offers, by the provider's ids; the first is its default.</summary>
    public IReadOnlyList<string> Models { get; init; } = [];

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
