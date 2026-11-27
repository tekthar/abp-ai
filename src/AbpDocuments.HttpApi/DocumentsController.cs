using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using AbpCluster.Storage;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Uow;

namespace AbpDocuments.HttpApi;

/// <summary>A change of a document's name.</summary>
public sealed record RenameDocumentInput(string Name);

/// <summary>A change of a document's class.</summary>
public sealed record ClassifyDocumentInput(string DataClass);

/// <summary>A search, as the API takes it.</summary>
public sealed record SearchDocumentsInput
{
    public string Query { get; init; } = "";

    public List<Guid>? ScopeIds { get; init; }

    public bool IncludeSite { get; init; }

    public List<Guid>? DocumentIds { get; init; }

    public int Take { get; init; } = 8;
}

/// <summary>
/// The documents a caller may read, as the host's rights and filter decide: listing them, adding a file, a document's
/// pages and file, renaming it, changing its class, reading it again, deleting it, and searching. A document the caller
/// may not read is one there isn't (404). No transaction is held around a request: the package's work brings its own.
/// </summary>
[Route("")]
[UnitOfWork(IsDisabled = true)]
public class DocumentsController(DocumentsService documents, DocumentsSearch search, IDocumentsCurrentActor actor) : AbpControllerBase
{
    /// <summary>What the library takes: kinds, extensions, the most bytes and pages, the classes and the default one.</summary>
    [HttpGet("settings")]
    public DocumentsSettings Settings() => documents.Settings();

    /// <summary>A scope's documents (<c>scopeId</c>; none: the site's own), or every one the caller may read (<c>all</c>), newest first.</summary>
    [HttpGet]
    public Task<DocumentsList> List([FromQuery] Guid? scopeId, [FromQuery] bool all = false, [FromQuery] string? search = null, [FromQuery] string? status = null,
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default) =>
        documents.ListAsync(actor.Get(), new DocumentsQuery { ScopeId = scopeId, AllScopes = all, Search = search, Status = status, Skip = skip, Take = take }, ct);

    /// <summary>Adds a file to a scope's library (multipart: <c>file</c>, <c>scopeId</c>, <c>dataClass</c>). It's read in the background.</summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(64L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 64L * 1024 * 1024)]
    public async Task<DocumentInfo> Add(IFormFile file, [FromForm] Guid? scopeId, [FromForm] string? dataClass, CancellationToken ct)
    {
        if (file is null)
        {
            throw new UserFriendlyException("Choose a file.", DocumentsErrorCodes.Invalid);
        }

        await using Stream content = file.OpenReadStream();
        return await documents.AddAsync(actor.Get(), scopeId, file.FileName, dataClass, content, ct);
    }

    [HttpGet("{id:guid}")]
    public async Task<DocumentInfo> Get(Guid id, CancellationToken ct) =>
        await documents.GetAsync(actor.Get(), id, ct) ?? throw Missing();

    /// <summary>The text of pages <c>from</c> to <c>to</c> (20 at most), each kept within the document's pages.</summary>
    [HttpGet("{id:guid}/pages")]
    public async Task<DocumentPages> Pages(Guid id, [FromQuery] int from = 1, [FromQuery] int to = 0, CancellationToken ct = default) =>
        await documents.PagesAsync(actor.Get(), id, from, to, ct) ?? throw Missing();

    /// <summary>The file: a PDF or a picture inline (for the viewer), anything else, or with <c>download</c>, as a download.</summary>
    [HttpGet("{id:guid}/file")]
    public async Task<IActionResult> File(Guid id, [FromQuery] bool download = false, CancellationToken ct = default)
    {
        DocumentFile file = await documents.FileAsync(actor.Get(), id, ct) ?? throw Missing();
        bool inline = !download && (file.Kind.Image || file.Kind == DocumentKinds.Pdf);
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.CacheControl = "private, no-store";
        if (inline)
        {
            // Shown in the browser's own viewer: the type its bytes say, nothing of the page runs, and it frames only on this site.
            Response.Headers.ContentSecurityPolicy = file.Kind.Image ? "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; sandbox" : "default-src 'none'; frame-ancestors 'self'";
            Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { FileName = Ascii(file.Name), Inline = true }.ToString()
                + "; filename*=UTF-8''" + Uri.EscapeDataString(file.Name);
            return File(file.Bytes, file.ContentType);
        }

        FileKinds.ServeSafely(Response);
        return File(file.Bytes, file.ContentType, file.Name);
    }

    /// <summary>Renames a document.</summary>
    [HttpPut("{id:guid}")]
    public Task<DocumentInfo> Rename(Guid id, [FromBody] RenameDocumentInput input, CancellationToken ct) =>
        documents.UpdateAsync(actor.Get(), id, new DocumentChange(Name: input.Name), ct);

    /// <summary>Changes a document's class, which decides which models may receive its text; its passages are embedded again.</summary>
    [HttpPut("{id:guid}/class"), DocumentsSensitive]
    public Task<DocumentInfo> Classify(Guid id, [FromBody] ClassifyDocumentInput input, CancellationToken ct) =>
        documents.UpdateAsync(actor.Get(), id, new DocumentChange(DataClass: input.DataClass), ct);

    /// <summary>Reads a document again from its file.</summary>
    [HttpPost("{id:guid}/reindex")]
    public Task<DocumentInfo> Reindex(Guid id, CancellationToken ct) => documents.ReindexAsync(actor.Get(), id, ct);

    [HttpDelete("{id:guid}")]
    public async Task<NoContentResult> Delete(Guid id, CancellationToken ct)
    {
        await documents.DeleteAsync(actor.Get(), id, ct);
        return NoContent();
    }

    /// <summary>The passages that best answer the words, by meaning and by words, among the documents the caller may read.</summary>
    [HttpPost("search")]
    public Task<SearchResult> Search([FromBody] SearchDocumentsInput input, CancellationToken ct) =>
        search.SearchAsync(actor.Get(), new SearchInput(input.Query)
        {
            ScopeIds = input.ScopeIds, IncludeSite = input.IncludeSite, DocumentIds = input.DocumentIds, Take = input.Take,
        }, ct);

    private static UserFriendlyException Missing() => new("There's no such document.", DocumentsErrorCodes.NotFound);

    private static string Ascii(string name) => new([.. name.Select(c => c is >= ' ' and <= '~' and not '"' ? c : '_')]);
}
