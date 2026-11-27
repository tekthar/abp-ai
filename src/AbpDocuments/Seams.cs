namespace AbpDocuments;

/// <summary>The scopes a person may read documents in: every scope, or some, with or without the site's own (scope null).</summary>
public sealed record DocumentsReach(bool AllScopes, IReadOnlyCollection<Guid> Scopes, bool Site)
{
    /// <summary>Nothing at all.</summary>
    public static readonly DocumentsReach None = new(false, [], false);

    /// <summary>Every document.</summary>
    public static readonly DocumentsReach Everything = new(true, [], true);

    public bool IsNone => !AllScopes && !Site && Scopes.Count == 0;
}

/// <summary>
/// Who may do what with documents, as the host decides: the scopes a person may read in, and where they may add and
/// manage. Every call of the package's asks it for the account it acts for. The default lets no one do anything.
/// </summary>
public interface IDocumentsRights
{
    /// <summary>The scopes whose documents the account may read; this goes into every query, before anything is ranked.</summary>
    Task<DocumentsReach> ReadableAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>Whether the account may add documents to the scope (null: the site's own).</summary>
    Task<bool> MayAddAsync(Guid accountId, Guid? scopeId, CancellationToken cancellationToken);

    /// <summary>Whether the account may rename, reclassify, read again and delete the scope's documents.</summary>
    Task<bool> MayManageAsync(Guid accountId, Guid? scopeId, CancellationToken cancellationToken);
}

/// <summary>
/// The host's own rules about which documents a person reads, beyond scopes (a restricted record, a parent's reach),
/// as a narrowing of the documents' query: it gets the host's own context, the one that replaces
/// <see cref="IDocumentsDbContext"/>, so it can reach the host's tables (an <c>EXISTS</c> against its records) and the
/// whole is one query with the ranking. The default narrows nothing.
/// </summary>
public interface IDocumentsFilter
{
    Task<IQueryable<Document>> FilterAsync(IQueryable<Document> documents, IDocumentsDbContext db, Guid accountId, CancellationToken cancellationToken);
}

/// <summary>A file the host keeps, as a source gives it to be read.</summary>
public sealed record DocumentFileContent(Stream Content, string? Version = null);

/// <summary>
/// A host's own files, read in place (an attachment's current version): the package asks for the bytes each time it
/// reads one, and never keeps a copy. Named by <see cref="Name"/>, the documents' <see cref="Document.Source"/>.
/// </summary>
public interface IDocumentSource
{
    string Name { get; }

    /// <summary>The file's bytes at its current version, or null when it's gone.</summary>
    Task<DocumentFileContent?> OpenAsync(string key, CancellationToken cancellationToken);
}

/// <summary>Something that happened to a document, for the host's audit.</summary>
/// <param name="Action">One of <see cref="DocumentActions"/>.</param>
/// <param name="By">Who did it; null for the package's own work.</param>
public sealed record DocumentEvent(string Action, Guid DocumentId, Guid? ScopeId, string Name, Guid? By)
{
    /// <summary>What changed, for a change: <c>name</c>, <c>dataClass</c>.</summary>
    public IReadOnlyDictionary<string, string?>? Changes { get; init; }
}

public static class DocumentActions
{
    public const string Added = "added";
    public const string Changed = "changed";
    public const string Deleted = "deleted";
    public const string Reindexed = "reindexed";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

/// <summary>Told of every change, for the host's audit and its own caches. The default does nothing.</summary>
public interface IDocumentsEvents
{
    Task OnAsync(DocumentEvent e, CancellationToken cancellationToken);
}

internal sealed class NoDocumentsRights : IDocumentsRights
{
    public Task<DocumentsReach> ReadableAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult(DocumentsReach.None);

    public Task<bool> MayAddAsync(Guid accountId, Guid? scopeId, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<bool> MayManageAsync(Guid accountId, Guid? scopeId, CancellationToken cancellationToken) => Task.FromResult(false);
}

internal sealed class NoDocumentsFilter : IDocumentsFilter
{
    public Task<IQueryable<Document>> FilterAsync(IQueryable<Document> documents, IDocumentsDbContext db, Guid accountId, CancellationToken cancellationToken) =>
        Task.FromResult(documents);
}

internal sealed class NoDocumentsEvents : IDocumentsEvents
{
    public Task OnAsync(DocumentEvent e, CancellationToken cancellationToken) => Task.CompletedTask;
}
