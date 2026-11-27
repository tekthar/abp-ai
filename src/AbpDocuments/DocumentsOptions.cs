namespace AbpDocuments;

/// <summary>How documents are kept, read and searched, from the <c>Documents</c> section or the host's code.</summary>
public sealed class DocumentsOptions
{
    public const string Section = "Documents";

    /// <summary>The most bytes a file may have: 25 MB.</summary>
    public long MaxBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>The most pages a document may have: 500.</summary>
    public int MaxPages { get; set; } = 500;

    /// <summary>The most pages of one document read by a model from their images: 100.</summary>
    public int MaxImagePages { get; set; } = 100;

    /// <summary>
    /// The data classes a document may have, lowest to highest, as AbpAi names them. Empty (the default): AbpAi's
    /// site classes except <c>aggregates</c> and <c>rows</c>, which are about data, not files.
    /// </summary>
    public List<string> DataClasses { get; set; } = [];

    /// <summary>A new document's class when its adder names none: <c>internal</c>.</summary>
    public string DefaultDataClass { get; set; } = "internal";

    /// <summary>The AbpAi alias of the model that reads scanned pages and pictures: <c>read</c>.</summary>
    public string ReadAlias { get; set; } = "read";

    /// <summary>The AbpAi alias of the embedding model: <c>embeddings</c>.</summary>
    public string EmbedAlias { get; set; } = "embeddings";

    /// <summary>The class a question counts as when it's embedded to be searched: <c>internal</c> (the organization's words).</summary>
    public string QuestionDataClass { get; set; } = "internal";

    /// <summary>About how long a passage is, in characters: 1,200.</summary>
    public int PassageLength { get; set; } = 1200;

    /// <summary>How much of a passage's end the next one starts with, in characters: 150.</summary>
    public int PassageOverlap { get; set; } = 150;

    /// <summary>How often a server's worker looks for documents to read: every 5 seconds.</summary>
    public TimeSpan PollEvery { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the embedding model's alias is checked for a change: every 10 minutes.</summary>
    public TimeSpan ModelCheckEvery { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The first wait after a model was busy or a limit reached; it doubles each time, up to <see cref="MaxRetryWait"/>.</summary>
    public TimeSpan RetryWait { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan MaxRetryWait { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Whether this server reads documents in the background. On by default; a host's web-only servers may turn it off.</summary>
    public bool Worker { get; set; } = true;
}

/// <summary>The package's fixed limits.</summary>
public static class DocumentsLimits
{
    public const int NameLength = 255;

    /// <summary>The most passages one search gives.</summary>
    public const int MaxTake = 30;

    /// <summary>The most pages one call gives.</summary>
    public const int MaxPagesAtOnce = 20;

    /// <summary>Candidates each way of ranking gives before they're merged.</summary>
    public const int Candidates = 50;

    /// <summary>Texts embedded in one call.</summary>
    public const int EmbedBatch = 64;
}

/// <summary>Where a document's bytes are.</summary>
public static class DocumentSources
{
    /// <summary>The package's own library: uploads, kept in AbpCluster.Storage's <see cref="DocumentsStore.Name"/> store.</summary>
    public const string Library = "library";
}

/// <summary>What the package asks AbpAi for, by purpose, so AbpAi's usage tells them apart.</summary>
public static class DocumentsPurposes
{
    public const string Read = "documents:read";
    public const string Embed = "documents:embed";
    public const string Search = "documents:search";
}

/// <summary>The package's error codes, which its API answers with their HTTP status.</summary>
public static class DocumentsErrorCodes
{
    /// <summary>The caller may not. 403.</summary>
    public const string Forbidden = "AbpDocuments:Forbidden";

    /// <summary>The input isn't right. 400.</summary>
    public const string Invalid = "AbpDocuments:Invalid";

    /// <summary>The file isn't a kind the package reads, or is over a limit. 400.</summary>
    public const string Unsupported = "AbpDocuments:Unsupported";

    /// <summary>There's no such document the caller may read. 404.</summary>
    public const string NotFound = "AbpDocuments:NotFound";
}
