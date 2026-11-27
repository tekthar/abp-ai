using NpgsqlTypes;

namespace AbpDocuments;

/// <summary>Where a document's reading stands.</summary>
public static class DocumentStatuses
{
    /// <summary>Waiting for a server to read it.</summary>
    public const string Queued = "queued";

    /// <summary>Its text is being taken out.</summary>
    public const string Reading = "reading";

    /// <summary>Its passages are being cut and embedded.</summary>
    public const string Indexing = "indexing";

    /// <summary>Searchable.</summary>
    public const string Ready = "ready";

    /// <summary>A model was busy or a limit was reached: tried again at <see cref="Document.RetryAt"/>.</summary>
    public const string Waiting = "waiting";

    /// <summary>It couldn't be read: <see cref="Document.Problem"/> says why.</summary>
    public const string Failed = "failed";

    /// <summary>Statuses a worker picks up (with <see cref="Waiting"/> once its time has come).</summary>
    public static readonly IReadOnlyList<string> Open = [Queued, Reading, Indexing];
}

/// <summary>What the next reading of a document does: read the file, or embed its kept text again.</summary>
public static class DocumentWork
{
    /// <summary>Read the file, cut its passages and embed them.</summary>
    public const string Read = "read";

    /// <summary>Embed its kept passages again (the embedding model changed).</summary>
    public const string Embed = "embed";
}

/// <summary>
/// A document: a file people added to the library (<see cref="Source"/> <c>library</c>), or one of the host's own files
/// it named (an attachment, keyed by its source and key, with its version). It belongs to a scope of the host's and has
/// a data class (AbpAi's), and its passages are what search ranks.
/// </summary>
public class Document
{
    protected Document()
    {
    }

    public Document(Guid id, string source, string sourceKey, Guid? scopeId, string name, string kind, string dataClass)
    {
        Id = id;
        Source = source;
        SourceKey = sourceKey;
        ScopeId = scopeId;
        Name = name;
        Kind = kind;
        DataClass = dataClass;
    }

    public Guid Id { get; private set; }

    /// <summary><see cref="DocumentSources.Library"/> for an upload; the host's source's name otherwise.</summary>
    public string Source { get; private set; } = null!;

    /// <summary>The file's key in its source: the document's id, in hex, for an upload.</summary>
    public string SourceKey { get; private set; } = null!;

    /// <summary>The source's version of the file, for a host's file that has versions; read again when it changes.</summary>
    public string? SourceVersion { get; set; }

    /// <summary>The host's scope (a project, an org unit); null for the site's own.</summary>
    public Guid? ScopeId { get; set; }

    /// <summary>What it's on, for a host's attachments: a kind (<c>record</c>) and the host's id.</summary>
    public string? OwnerKind { get; set; }

    public string? OwnerId { get; set; }

    /// <summary>The file's name, as people see it.</summary>
    public string Name { get; set; } = null!;

    /// <summary>What its bytes are: one of <see cref="DocumentKinds"/>' names.</summary>
    public string Kind { get; set; } = null!;

    public long Size { get; set; }

    /// <summary>The bytes' SHA-256, in hex.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>The highest class of what it says, as AbpAi names classes: where its text may go.</summary>
    public string DataClass { get; set; } = null!;

    /// <summary>Who added it; null once their name is forgotten, or for a host's file added by no one.</summary>
    public Guid? AddedBy { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    public string Status { get; set; } = DocumentStatuses.Queued;

    /// <summary>What the next reading does (<see cref="DocumentWork"/>).</summary>
    public string Work { get; set; } = DocumentWork.Read;

    /// <summary>Why it failed, or what was left out of a reading that worked (searched by its words only).</summary>
    public string? Problem { get; set; }

    /// <summary>When a waiting document is tried again.</summary>
    public DateTime? RetryAt { get; set; }

    /// <summary>Readings that waited in a row, for the next wait's length.</summary>
    public int Attempts { get; set; }

    /// <summary>Its pages, once read.</summary>
    public int Pages { get; set; }

    /// <summary>Its passages, once cut.</summary>
    public int Passages { get; set; }

    /// <summary>Pages read by a model that reads images (scanned pages, a picture).</summary>
    public int PagesFromImages { get; set; }

    /// <summary>The embedding model its passages' vectors came from; null when none may embed them.</summary>
    public string? Model { get; set; }

    /// <summary>When it was last read.</summary>
    public DateTime? ReadAt { get; set; }

    /// <summary>Out of search and lists, its passages kept (a record in the trash).</summary>
    public bool Hidden { get; set; }

    /// <summary>Changes with every change of the row, so two servers never both write one reading.</summary>
    public Guid Stamp { get; set; } = Guid.NewGuid();
}

/// <summary>A page's text, as read: headings as Markdown's <c>#</c> lines.</summary>
public class DocumentPage
{
    protected DocumentPage()
    {
    }

    public DocumentPage(Guid documentId, int number, string text, bool fromImage)
    {
        DocumentId = documentId;
        Number = number;
        Text = text;
        FromImage = fromImage;
    }

    public Guid DocumentId { get; private set; }

    /// <summary>From 1.</summary>
    public int Number { get; private set; }

    public string Text { get; private set; } = null!;

    /// <summary>Read by a model from the page's image.</summary>
    public bool FromImage { get; private set; }
}

/// <summary>A passage of a page: what search ranks and an answer cites.</summary>
public class DocumentPassage
{
    protected DocumentPassage()
    {
    }

    public DocumentPassage(Guid id, Guid documentId, int page, int ordinal, string? heading, string text)
    {
        Id = id;
        DocumentId = documentId;
        Page = page;
        Ordinal = ordinal;
        Heading = heading;
        Text = text;
    }

    public Guid Id { get; private set; }

    public Guid DocumentId { get; private set; }

    /// <summary>The page it's on, from 1.</summary>
    public int Page { get; private set; }

    /// <summary>Its place in the document, from 0.</summary>
    public int Ordinal { get; private set; }

    /// <summary>The heading it sits under, if any.</summary>
    public string? Heading { get; private set; }

    public string Text { get; private set; } = null!;

    /// <summary>The source's version it came from.</summary>
    public string? SourceVersion { get; set; }

    /// <summary>The embedding model of <see cref="Embedding"/>.</summary>
    public string? Model { get; set; }

    /// <summary>Its vector (<c>real[]</c>, compared as pgvector's <c>vector</c>); null when no model may embed it.</summary>
    public float[]? Embedding { get; set; }

    /// <summary>Its words for full-text search: generated by the database from its heading and text.</summary>
    public NpgsqlTsVector Search { get; private set; } = null!;
}
