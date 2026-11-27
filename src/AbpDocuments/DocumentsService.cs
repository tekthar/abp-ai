using System.Security.Cryptography;
using AbpAi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.BlobStoring;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Uow;

namespace AbpDocuments;

/// <summary>A document as people see it in a list.</summary>
public sealed record DocumentInfo(
    Guid Id,
    Guid? ScopeId,
    string Name,
    string Kind,
    string ContentType,
    long Size,
    string DataClass,
    string Status,
    string? Problem,
    DateTime? RetryAt,
    int Pages,
    int Passages,
    int PagesFromImages,
    Guid? AddedBy,
    DateTime AddedAt,
    DateTime? ReadAt,
    string Source,
    string? OwnerKind,
    string? OwnerId,
    bool Hidden)
{
    internal static DocumentInfo Of(Document d) => new(d.Id, d.ScopeId, d.Name, d.Kind, DocumentKinds.Named(d.Kind)?.ContentType ?? "application/octet-stream",
        d.Size, d.DataClass, d.Status, d.Problem, d.RetryAt, d.Pages, d.Passages, d.PagesFromImages, d.AddedBy, d.AddedAt, d.ReadAt, d.Source,
        d.OwnerKind, d.OwnerId, d.Hidden);
}

/// <summary>Which documents to list: a scope's (or every one the caller may read), their names' words, a status.</summary>
public sealed record DocumentsQuery
{
    /// <summary>One scope's documents; with <see cref="AllScopes"/> false, null is the site's own.</summary>
    public Guid? ScopeId { get; init; }

    /// <summary>Every document the caller may read, whatever its scope.</summary>
    public bool AllScopes { get; init; }

    /// <summary>Words of the name.</summary>
    public string? Search { get; init; }

    /// <summary>Only documents in this status.</summary>
    public string? Status { get; init; }

    public int Skip { get; init; }

    public int Take { get; init; } = 50;
}

/// <summary>A page of documents and how many there are in all.</summary>
public sealed record DocumentsList(IReadOnlyList<DocumentInfo> Items, int Total);

/// <summary>A page's text as read.</summary>
public sealed record DocumentPageText(int Number, string Text, bool FromImage);

/// <summary>A document's pages from <c>From</c> to <c>To</c>, of all it has.</summary>
public sealed record DocumentPages(DocumentInfo Document, int From, int To, IReadOnlyList<DocumentPageText> Pages);

/// <summary>A file to send: its bytes, true type and name.</summary>
public sealed record DocumentFile(byte[] Bytes, string ContentType, string Name, DocumentKind Kind);

/// <summary>A change to a document: its name, its data class, or both.</summary>
public sealed record DocumentChange(string? Name = null, string? DataClass = null);

/// <summary>A host's own file to read in place: its source and key, its version, where it belongs and what it is.</summary>
public sealed record SourceDocument(string Source, string Key, string? Version, Guid? ScopeId, string Name, string? DataClass = null)
{
    public string? OwnerKind { get; init; }

    public string? OwnerId { get; init; }

    public Guid? AddedBy { get; init; }
}

/// <summary>What the library takes: the kinds, the limits and the classes, for an upload's form.</summary>
public sealed record DocumentsSettings(long MaxBytes, int MaxPages, IReadOnlyList<string> Kinds, IReadOnlyList<string> Extensions,
    IReadOnlyList<string> DataClasses, string DefaultDataClass);

/// <summary>Where uploads are kept: AbpCluster.Storage's store <c>documents</c>, under each document's id.</summary>
[BlobContainerName(Name)]
public sealed class DocumentsStore
{
    public const string Name = "documents";

    public static string BlobOf(Guid id) => id.ToString("N");
}

/// <summary>
/// Documents for people and for the host: adding a file (checked by its bytes), listing what someone may read, a
/// document's pages and file, changing its name or class, reading it again, deleting it; and for the host, its own files
/// read in place, hiding them, and forgetting a scope or an account. Every call for a person names the account it acts
/// for and goes through the host's rights and filter.
/// </summary>
public class DocumentsService(
    DocumentsDb documentsDb,
    IDocumentsRights rights,
    IDocumentsFilter filter,
    IDocumentsEvents events,
    IBlobContainer<DocumentsStore> blobs,
    IEnumerable<IDocumentSource> sources,
    DocumentsSignal signal,
    AiDataRules dataRules,
    IUnitOfWorkManager units,
    IOptions<DocumentsOptions> options) : ITransientDependency
{
    private DocumentsOptions O => options.Value;

    // ---- What the library takes.

    /// <summary>The classes a document may have, lowest to highest: the options', else the site's without aggregates and rows.</summary>
    public IReadOnlyList<string> DataClasses => O.DataClasses is { Count: > 0 } own
        ? own
        : [.. dataRules.Classes.Where(c => c is not (AiDataClasses.Aggregates or AiDataClasses.Rows))];

    public DocumentsSettings Settings() => new(O.MaxBytes, O.MaxPages, [.. DocumentKinds.All.Select(k => k.Name)], DocumentKinds.Extensions, DataClasses,
        ClassOf(null));

    /// <summary>A class as the site names it; the default for none; refused when it isn't one a document may have.</summary>
    public string ClassOf(string? dataClass)
    {
        string wanted = string.IsNullOrWhiteSpace(dataClass) ? O.DefaultDataClass : dataClass.Trim();
        return DataClasses.FirstOrDefault(c => string.Equals(c, wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new UserFriendlyException($"\"{wanted}\" isn't a class a document may have ({string.Join(", ", DataClasses)}).", DocumentsErrorCodes.Invalid);
    }

    private bool Below(string cls, string than)
    {
        List<string> order = [.. DataClasses];
        return order.IndexOf(cls) < order.IndexOf(than);
    }

    // ---- For people.

    /// <summary>Adds an uploaded file to a scope's library, once it's known by its bytes and within the limits. It's read in the background.</summary>
    public async Task<DocumentInfo> AddAsync(Guid accountId, Guid? scopeId, string fileName, string? dataClass, Stream content, CancellationToken cancellationToken)
    {
        if (!await InUnitAsync(() => rights.MayAddAsync(accountId, scopeId, cancellationToken)))
        {
            throw new UserFriendlyException("You may not add documents here.", DocumentsErrorCodes.Forbidden);
        }

        string cls = ClassOf(dataClass);
        // An adder files a document at the default class or a stricter one; a less strict class (its text going to more
        // models) is the scope's managers' to give.
        if (Below(cls, ClassOf(null)) && !await InUnitAsync(() => rights.MayManageAsync(accountId, scopeId, cancellationToken)))
        {
            throw new UserFriendlyException($"Only those who manage this place may file a document as {cls}: choose {ClassOf(null)} or stricter.",
                DocumentsErrorCodes.Forbidden);
        }

        byte[] bytes = await ReadAllAsync(content, O.MaxBytes, cancellationToken);
        if (bytes.Length == 0)
        {
            throw new UserFriendlyException("The file is empty.", DocumentsErrorCodes.Unsupported);
        }

        DocumentKind kind = DocumentKinds.Sniff(bytes, fileName)
            ?? throw new UserFriendlyException("A document is a PDF, a Word file (.docx), text or Markdown, or a PNG, JPEG or WebP picture.", DocumentsErrorCodes.Unsupported);
        Guid id = Guid.NewGuid();
        Document document = new(id, DocumentSources.Library, DocumentsStore.BlobOf(id), scopeId, NameOf(fileName, kind), kind.Name, cls)
        {
            Size = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            AddedBy = accountId,
        };
        await blobs.SaveAsync(DocumentsStore.BlobOf(id), bytes, overrideExisting: true, cancellationToken);
        try
        {
            await documentsDb.RunAsync(async db =>
            {
                db.Documents.Add(document);
                await db.SaveChangesAsync(cancellationToken);
            }, cancellationToken: cancellationToken);
        }
        catch
        {
            await blobs.DeleteAsync(DocumentsStore.BlobOf(id), CancellationToken.None);
            throw;
        }

        await events.OnAsync(new DocumentEvent(DocumentActions.Added, id, scopeId, document.Name, accountId), cancellationToken);
        signal.Notify();
        return DocumentInfo.Of(document);
    }

    /// <summary>The documents the account may read: one scope's, or all, newest first.</summary>
    public async Task<DocumentsList> ListAsync(Guid accountId, DocumentsQuery query, CancellationToken cancellationToken)
    {
        int take = Math.Clamp(query.Take, 1, 200);
        int skip = Math.Max(0, query.Skip);
        return await documentsDb.RunAsync(async db =>
        {
            IQueryable<Document> docs = await ReadableAsync(db, accountId, cancellationToken);
            if (!query.AllScopes)
            {
                docs = query.ScopeId is { } scope ? docs.Where(d => d.ScopeId == scope) : docs.Where(d => d.ScopeId == null);
            }

            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                foreach (string word in query.Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(5))
                {
                    string like = "%" + word.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
                    docs = docs.Where(d => EF.Functions.ILike(d.Name, like, "\\"));
                }
            }

            if (!string.IsNullOrWhiteSpace(query.Status))
            {
                docs = docs.Where(d => d.Status == query.Status);
            }

            int total = await docs.CountAsync(cancellationToken);
            List<Document> page = await docs.OrderByDescending(d => d.AddedAt).ThenBy(d => d.Id).Skip(skip).Take(take).AsNoTracking().ToListAsync(cancellationToken);
            return new DocumentsList([.. page.Select(DocumentInfo.Of)], total);
        }, cancellationToken: cancellationToken);
    }

    /// <summary>A document the account may read; null when there's none (or they may not read it).</summary>
    public Task<DocumentInfo?> GetAsync(Guid accountId, Guid id, CancellationToken cancellationToken) =>
        documentsDb.RunAsync(async db => await FindReadableAsync(db, accountId, id, cancellationToken) is { } d ? DocumentInfo.Of(d) : null,
            cancellationToken: cancellationToken);

    /// <summary>
    /// A document's pages' text, <paramref name="from"/> to <paramref name="to"/> (each kept within its pages, 20 at
    /// most); null when the account may not read it.
    /// </summary>
    public Task<DocumentPages?> PagesAsync(Guid accountId, Guid id, int from, int to, CancellationToken cancellationToken) =>
        documentsDb.RunAsync<DocumentPages?>(async db =>
        {
            if (await FindReadableAsync(db, accountId, id, cancellationToken) is not { } d)
            {
                return null;
            }

            int last = Math.Max(1, d.Pages);
            int first = Math.Clamp(from, 1, last);
            int end = Math.Clamp(to < first ? first : to, first, Math.Min(last, first + DocumentsLimits.MaxPagesAtOnce - 1));
            List<DocumentPageText> pages = await db.DocumentPages.AsNoTracking()
                .Where(p => p.DocumentId == id && p.Number >= first && p.Number <= end)
                .OrderBy(p => p.Number)
                .Select(p => new DocumentPageText(p.Number, p.Text, p.FromImage))
                .ToListAsync(cancellationToken);
            return new DocumentPages(DocumentInfo.Of(d), first, end, pages);
        }, cancellationToken: cancellationToken);

    /// <summary>A document's file, its type by its bytes; null when the account may not read it, or it's gone.</summary>
    public async Task<DocumentFile?> FileAsync(Guid accountId, Guid id, CancellationToken cancellationToken)
    {
        Document? d = await documentsDb.RunAsync(db => FindReadableAsync(db, accountId, id, cancellationToken), cancellationToken: cancellationToken);
        if (d is null || await BytesAsync(d, cancellationToken) is not { } read)
        {
            return null;
        }

        DocumentKind? kind = DocumentKinds.Sniff(read.Bytes, d.Name);
        return kind is null ? null : new DocumentFile(read.Bytes, kind.ContentType, d.Name, kind);
    }

    /// <summary>Renames a document or changes its class; a new class embeds its passages again, through a model allowed it.</summary>
    public async Task<DocumentInfo> UpdateAsync(Guid accountId, Guid id, DocumentChange change, CancellationToken cancellationToken)
    {
        Document d = await ManagedAsync(accountId, id, allowAdder: false, cancellationToken);
        string? name = change.Name is { } n ? NameOf(n, null) : null;
        string? cls = change.DataClass is { } c ? ClassOf(c) : null;
        Dictionary<string, string?> changes = [];
        Document saved = await documentsDb.RunAsync(async db =>
        {
            Document row = await db.Documents.FirstAsync(x => x.Id == id, cancellationToken);
            if (name is not null && name != row.Name)
            {
                changes["name"] = name;
                row.Name = name;
            }

            if (cls is not null && cls != row.DataClass)
            {
                changes["dataClass"] = cls;
                row.DataClass = cls;
                if (row.Status is DocumentStatuses.Ready)
                {
                    row.Status = DocumentStatuses.Queued;
                    row.Work = DocumentWork.Embed;
                }
            }

            row.Stamp = Guid.NewGuid();
            await db.SaveChangesAsync(cancellationToken);
            return row;
        }, cancellationToken: cancellationToken);
        if (changes.Count > 0)
        {
            await events.OnAsync(new DocumentEvent(DocumentActions.Changed, id, d.ScopeId, saved.Name, accountId) { Changes = changes }, cancellationToken);
            signal.Notify();
        }

        return DocumentInfo.Of(saved);
    }

    /// <summary>Reads a document again from its file: after a failure, or to take a better model's reading.</summary>
    public async Task<DocumentInfo> ReindexAsync(Guid accountId, Guid id, CancellationToken cancellationToken)
    {
        Document d = await ManagedAsync(accountId, id, allowAdder: false, cancellationToken);
        Document saved = await RequeueAsync(id, DocumentWork.Read, cancellationToken);
        await events.OnAsync(new DocumentEvent(DocumentActions.Reindexed, id, d.ScopeId, d.Name, accountId), cancellationToken);
        signal.Notify();
        return DocumentInfo.Of(saved);
    }

    /// <summary>Deletes a document: its passages and pages with it, then its file. The scope's managers may, and whoever added it.</summary>
    public async Task DeleteAsync(Guid accountId, Guid id, CancellationToken cancellationToken)
    {
        Document d = await ManagedAsync(accountId, id, allowAdder: true, cancellationToken);
        await RemoveAsync([d], cancellationToken);
        await events.OnAsync(new DocumentEvent(DocumentActions.Deleted, id, d.ScopeId, d.Name, accountId), cancellationToken);
    }

    // ---- For the host.

    /// <summary>
    /// Names one of the host's own files to be read in place, or brings it up to date: a new version is read again and
    /// its passages replace the old; a new name, scope, owner or class is kept as is. Unchecked: the host decides.
    /// </summary>
    public async Task<DocumentInfo> PutFromSourceAsync(SourceDocument file, CancellationToken cancellationToken)
    {
        if (file.Source == DocumentSources.Library || !sources.Any(s => s.Name == file.Source))
        {
            throw new UserFriendlyException($"There's no document source \"{file.Source}\".", DocumentsErrorCodes.Invalid);
        }

        string cls = ClassOf(file.DataClass);
        string name = NameOf(file.Name, null);
        (Document saved, bool added) = await documentsDb.RunAsync(async db =>
        {
            Document? row = await db.Documents.FirstOrDefaultAsync(d => d.Source == file.Source && d.SourceKey == file.Key, cancellationToken);
            bool isNew = row is null;
            if (row is null)
            {
                row = new Document(Guid.NewGuid(), file.Source, file.Key, file.ScopeId, name, DocumentKinds.Text.Name, cls) { AddedBy = file.AddedBy, SourceVersion = file.Version };
                db.Documents.Add(row);
            }
            else
            {
                bool newVersion = row.SourceVersion != file.Version;
                bool newClass = row.DataClass != cls;
                row.ScopeId = file.ScopeId;
                row.Name = name;
                row.DataClass = cls;
                row.SourceVersion = file.Version;
                if (newVersion)
                {
                    Queue(row, DocumentWork.Read);
                }
                else if (newClass && row.Status == DocumentStatuses.Ready)
                {
                    Queue(row, DocumentWork.Embed);
                }

                row.Stamp = Guid.NewGuid();
            }

            row.OwnerKind = file.OwnerKind;
            row.OwnerId = file.OwnerId;
            await db.SaveChangesAsync(cancellationToken);
            return (row, isNew);
        }, cancellationToken: cancellationToken);
        if (added)
        {
            await events.OnAsync(new DocumentEvent(DocumentActions.Added, saved.Id, saved.ScopeId, saved.Name, file.AddedBy), cancellationToken);
        }

        signal.Notify();
        return DocumentInfo.Of(saved);
    }

    /// <summary>Takes a host's file out of search and lists, or puts it back, its passages kept. False when there's none.</summary>
    public Task<bool> SetHiddenAsync(string source, string key, bool hidden, CancellationToken cancellationToken) =>
        documentsDb.RunAsync(async db => await db.Documents.Where(d => d.Source == source && d.SourceKey == key)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Hidden, hidden).SetProperty(d => d.Stamp, Guid.NewGuid()), cancellationToken) > 0,
            cancellationToken: cancellationToken);

    /// <summary>Forgets a host's file: its passages and pages go. False when there's none.</summary>
    public async Task<bool> RemoveFromSourceAsync(string source, string key, CancellationToken cancellationToken)
    {
        List<Document> found = await documentsDb.RunAsync(db => db.Documents.AsNoTracking().Where(d => d.Source == source && d.SourceKey == key).ToListAsync(cancellationToken),
            cancellationToken: cancellationToken);
        await RemoveAsync(found, cancellationToken);
        return found.Count > 0;
    }

    /// <summary>For a scope the host purged: every document in it, its passages, pages and files. How many went.</summary>
    public async Task<int> ForgetScopeAsync(Guid scopeId, CancellationToken cancellationToken)
    {
        List<Document> found = await documentsDb.RunAsync(db => db.Documents.AsNoTracking().Where(d => d.ScopeId == scopeId).ToListAsync(cancellationToken),
            cancellationToken: cancellationToken);
        await RemoveAsync(found, cancellationToken);
        return found.Count;
    }

    /// <summary>Whether any document names the account as the one who added it (an identity package's "account in use").</summary>
    public Task<bool> NamesAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
        documentsDb.RunAsync(db => db.Documents.AnyAsync(d => d.AddedBy == accountId, cancellationToken), cancellationToken: cancellationToken);

    /// <summary>For erasure: the account's name goes from what it added (the documents stay, added by no one). How many.</summary>
    public Task<int> ForgetAccountAsync(Guid accountId, CancellationToken cancellationToken) =>
        documentsDb.RunAsync(db => db.Documents.Where(d => d.AddedBy == accountId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.AddedBy, (Guid?)null), cancellationToken), cancellationToken: cancellationToken);

    // ---- Shared.

    /// <summary>
    /// The documents the account may read, not hidden: the host's scopes for them, then its own filter, as one query a
    /// caller adds to. Nothing is ranked or read outside it.
    /// </summary>
    internal async Task<IQueryable<Document>> ReadableAsync(IDocumentsDbContext db, Guid accountId, CancellationToken ct)
    {
        DocumentsReach reach = await rights.ReadableAsync(accountId, ct);
        IQueryable<Document> docs = db.Documents.Where(d => !d.Hidden);
        if (reach.IsNone)
        {
            return docs.Where(d => false);
        }

        if (!reach.AllScopes)
        {
            Guid[] scopes = [.. reach.Scopes.Distinct()];
            bool site = reach.Site;
            docs = docs.Where(d => (site && d.ScopeId == null) || (d.ScopeId != null && scopes.Contains(d.ScopeId.Value)));
        }
        else if (!reach.Site)
        {
            docs = docs.Where(d => d.ScopeId != null);
        }

        return await filter.FilterAsync(docs, db, accountId, ct);
    }

    private async Task<Document?> FindReadableAsync(IDocumentsDbContext db, Guid accountId, Guid id, CancellationToken ct) =>
        await (await ReadableAsync(db, accountId, ct)).AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);

    /// <summary>A document the account may read and manage (the adder too, when <paramref name="allowAdder"/>); refused otherwise.</summary>
    private async Task<Document> ManagedAsync(Guid accountId, Guid id, bool allowAdder, CancellationToken ct)
    {
        Document d = await documentsDb.RunAsync(db => FindReadableAsync(db, accountId, id, ct), cancellationToken: ct)
            ?? throw new UserFriendlyException("There's no such document.", DocumentsErrorCodes.NotFound);
        if ((allowAdder && d.AddedBy == accountId) || await InUnitAsync(() => rights.MayManageAsync(accountId, d.ScopeId, ct)))
        {
            return d;
        }

        throw new UserFriendlyException("You may not change this document.", DocumentsErrorCodes.Forbidden);
    }

    /// <summary>
    /// A host's seam, inside a unit of work: its rights may read its own tables, and a caller (an API action, an agent's
    /// tool) needn't have one. One already there is joined.
    /// </summary>
    private async Task<T> InUnitAsync<T>(Func<Task<T>> work)
    {
        using IUnitOfWork uow = units.Begin(new AbpUnitOfWorkOptions { IsTransactional = false });
        T result = await work();
        await uow.CompleteAsync();
        return result;
    }

    internal Task<Document> RequeueAsync(Guid id, string work, CancellationToken ct) =>
        documentsDb.RunAsync(async db =>
        {
            Document row = await db.Documents.FirstAsync(d => d.Id == id, ct);
            Queue(row, work);
            row.Stamp = Guid.NewGuid();
            await db.SaveChangesAsync(ct);
            return row;
        }, cancellationToken: ct);

    private static void Queue(Document row, string work)
    {
        row.Status = DocumentStatuses.Queued;
        row.Work = work;
        row.Problem = null;
        row.RetryAt = null;
        row.Attempts = 0;
    }

    private async Task RemoveAsync(IReadOnlyCollection<Document> documents, CancellationToken ct)
    {
        if (documents.Count == 0)
        {
            return;
        }

        Guid[] ids = [.. documents.Select(d => d.Id)];
        await documentsDb.RunAsync(async db =>
        {
            await db.DocumentPassages.Where(p => ids.Contains(p.DocumentId)).ExecuteDeleteAsync(ct);
            await db.DocumentPages.Where(p => ids.Contains(p.DocumentId)).ExecuteDeleteAsync(ct);
            await db.Documents.Where(d => ids.Contains(d.Id)).ExecuteDeleteAsync(ct);
        }, isTransactional: true, ct);
        foreach (Document d in documents.Where(d => d.Source == DocumentSources.Library))
        {
            await blobs.DeleteAsync(DocumentsStore.BlobOf(d.Id), ct);
        }
    }

    /// <summary>A document's bytes, from the library or its host's source, with the version the source gave.</summary>
    internal async Task<(byte[] Bytes, string? Version)?> BytesAsync(Document d, CancellationToken ct)
    {
        if (d.Source == DocumentSources.Library)
        {
            return await blobs.GetAllBytesOrNullAsync(DocumentsStore.BlobOf(d.Id), ct) is { } bytes ? (bytes, null) : null;
        }

        IDocumentSource? source = sources.LastOrDefault(s => s.Name == d.Source);
        if (source is null || await source.OpenAsync(d.SourceKey, ct) is not { } file)
        {
            return null;
        }

        await using (file.Content)
        {
            return (await ReadAllAsync(file.Content, O.MaxBytes, ct), file.Version);
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream content, long max, CancellationToken ct)
    {
        using MemoryStream read = new();
        byte[] buffer = new byte[81920];
        int got;
        while ((got = await content.ReadAsync(buffer, ct)) > 0)
        {
            if (read.Length + got > max)
            {
                throw new UserFriendlyException($"A document is at most {max / (1024 * 1024)} MB.", DocumentsErrorCodes.Unsupported);
            }

            read.Write(buffer, 0, got);
        }

        return read.ToArray();
    }

    /// <summary>A file's name as people see it: no folders or control characters, at most 255 characters, with its kind's extension when it had none.</summary>
    internal static string NameOf(string fileName, DocumentKind? kind)
    {
        string name = new([.. Path.GetFileName((fileName ?? "").Replace('\\', '/')).Where(c => !char.IsControl(c))]);
        name = name.Trim();
        if (name.Length == 0)
        {
            name = "document" + (kind is null ? "" : "." + kind.Extension);
        }
        else if (kind is not null && Path.GetExtension(name).Length == 0)
        {
            name += "." + kind.Extension;
        }

        return name.Length <= DocumentsLimits.NameLength ? name : name[..DocumentsLimits.NameLength];
    }
}
