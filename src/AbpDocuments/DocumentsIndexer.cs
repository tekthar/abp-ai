using System.Security.Cryptography;
using AbpAi;
using AbpDocuments.Reading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace AbpDocuments;

/// <summary>Wakes a server's worker at once, after this server took an upload or a change.</summary>
public sealed class DocumentsSignal : ISingletonDependency
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    public void Notify()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already woken.
            }
        }
    }

    internal Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _wake.WaitAsync(timeout, ct);
}

/// <summary>
/// Reads one document, under its cluster lock (the worker takes it): its file's pages (a model reads its images),
/// then its passages, embedded through AbpAi. A model that's busy or a limit reached leaves it waiting, to be tried
/// again; a file that can't be read, or a model that may not have it, ends it failed with the reason. No embedding
/// model allowed its class leaves it searchable by its words alone.
/// </summary>
public class DocumentsIndexer(
    DocumentsDb documentsDb,
    DocumentsService documents,
    ImageText images,
    IAi ai,
    IDocumentsEvents events,
    IOptions<DocumentsOptions> options,
    ILogger<DocumentsIndexer> logger) : ITransientDependency
{
    private DocumentsOptions O => options.Value;

    /// <summary>Whether a refusal is a wait (a limit, a busy or slow provider) rather than a failure.</summary>
    internal static bool IsWait(AiRefusedException e) =>
        e.Code is AiRefusalCodes.OverLimit or AiRefusalCodes.Provider or AiRefusalCodes.Timeout;

    /// <summary>Reads the document if it's still one to read: true when it ran, whatever came of it.</summary>
    public async Task<bool> RunAsync(Guid id, CancellationToken cancellationToken)
    {
        Document? doc = await documentsDb.RunAsync(db => db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, cancellationToken),
            cancellationToken: cancellationToken);
        DateTime now = DateTime.UtcNow;
        if (doc is null || !(DocumentStatuses.Open.Contains(doc.Status) || (doc.Status == DocumentStatuses.Waiting && doc.RetryAt <= now)))
        {
            return false;
        }

        Guid stamp = doc.Stamp;
        bool embedOnly = doc.Work == DocumentWork.Embed && doc.Pages > 0;
        if (await MarkAsync(id, stamp, embedOnly ? DocumentStatuses.Indexing : DocumentStatuses.Reading, cancellationToken) is not { } marked)
        {
            return false;
        }

        stamp = marked;
        // A file that takes too long (a hostile PDF) fails rather than holding its lock: past ReadTimeout, the reading is
        // left (its thread finishes on its own) and the document ends failed.
        using CancellationTokenSource reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        reading.CancelAfter(O.ReadTimeout);
        CancellationToken ct = reading.Token;
        try
        {
            List<(int Number, string Text)> pages;
            string? version = doc.SourceVersion;
            if (embedOnly)
            {
                pages = await documentsDb.RunAsync(db => db.DocumentPages.AsNoTracking().Where(p => p.DocumentId == id).OrderBy(p => p.Number)
                    .Select(p => new ValueTuple<int, string>(p.Number, p.Text)).ToListAsync(ct), cancellationToken: ct);
            }
            else
            {
                (pages, version, stamp) = await ReadPagesAsync(doc, stamp, ct);
            }

            IReadOnlyList<CutPassage> cut = Passages.Cut(pages, O.PassageLength, O.PassageOverlap);
            (List<float[]>? vectors, string? model, string? problem) = await EmbedAsync(doc, cut, ct);
            await SaveAsync(doc, stamp, cut, vectors, model, problem, version, ct);
            await events.OnAsync(new DocumentEvent(DocumentActions.Ready, id, doc.ScopeId, doc.Name, null), cancellationToken);
        }
        catch (AiRefusedException e) when (IsWait(e))
        {
            TimeSpan wait = TimeSpan.FromTicks(Math.Min(O.MaxRetryWait.Ticks, O.RetryWait.Ticks * (1L << Math.Min(doc.Attempts, 20))));
            logger.LogInformation("Document {Id} waits {Wait} to be read again: {Reason}", id, wait, e.Message);
            await EndAsync(id, stamp, DocumentStatuses.Waiting, e.Message, DateTime.UtcNow + wait, doc.Attempts + 1, cancellationToken);
        }
        catch (AiRefusedException e)
        {
            await FailAsync(doc, stamp, e.Message, cancellationToken);
        }
        catch (DocumentUnreadableException e)
        {
            await FailAsync(doc, stamp, e.Message, cancellationToken);
        }
        catch (OperationCanceledException) when (reading.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            await FailAsync(doc, stamp, $"Reading it took longer than {O.ReadTimeout.TotalMinutes:0.#} minutes.", cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Changed or deleted while it was read: the change wins, and the next round reads it again if it must.
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (e is DbUpdateException && !await ExistsAsync(id, cancellationToken))
            {
                return true; // Deleted while it was read.
            }

            logger.LogWarning(e, "Document {Id} couldn't be read.", id);
            await FailAsync(doc, stamp, "It couldn't be read: " + e.Message, cancellationToken);
        }

        return true;
    }

    private async Task<(List<(int, string)> Pages, string? Version, Guid Stamp)> ReadPagesAsync(Document doc, Guid stamp, CancellationToken ct)
    {
        if (await documents.BytesAsync(doc, ct) is not { } read)
        {
            throw new DocumentUnreadableException("Its file is gone.");
        }

        DocumentKind kind = DocumentKinds.Sniff(read.Bytes, doc.Name)
            ?? throw new DocumentUnreadableException("Its file isn't a kind that's read: a PDF, a Word file, text or Markdown, or a PNG, JPEG or WebP picture.");
        // On a thread of its own, so the time limit holds even while a parser doesn't look at the token.
        IReadOnlyList<SourcePage> source = await Task.Run(() => PageReaders.Read(kind, read.Bytes, ReadLimits.Of(O), ct), ct).WaitAsync(ct);
        int imagePages = source.Count(p => p.NeedsModel);
        if (imagePages > O.MaxImagePages)
        {
            throw new DocumentUnreadableException($"It has {imagePages} scanned pages; at most {O.MaxImagePages} are read.");
        }

        List<DocumentPage> pages = [];
        foreach (SourcePage page in source)
        {
            string text = page.NeedsModel
                ? await images.ReadAsync(doc, page.Image!, page.ImageType!, page.Number, ct)
                : page.Text;
            pages.Add(new DocumentPage(doc.Id, page.Number, text, page.NeedsModel));
        }

        Guid next = Guid.NewGuid();
        await documentsDb.RunAsync(async db =>
        {
            int updated = await db.Documents.Where(d => d.Id == doc.Id && d.Stamp == stamp).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, DocumentStatuses.Indexing)
                // Its pages are kept: a wait from here on embeds them again, without reading the file (and its images) again.
                .SetProperty(d => d.Work, DocumentWork.Embed)
                .SetProperty(d => d.Kind, kind.Name)
                .SetProperty(d => d.Size, read.Bytes.LongLength)
                .SetProperty(d => d.Sha256, Convert.ToHexStringLower(SHA256.HashData(read.Bytes)))
                .SetProperty(d => d.Pages, pages.Count)
                .SetProperty(d => d.PagesFromImages, imagePages)
                .SetProperty(d => d.Stamp, next), ct);
            if (updated == 0)
            {
                throw new DbUpdateConcurrencyException("The document changed while it was read.");
            }

            await db.DocumentPages.Where(p => p.DocumentId == doc.Id).ExecuteDeleteAsync(ct);
            db.DocumentPages.AddRange(pages);
            await db.SaveChangesAsync(ct);
        }, isTransactional: true, ct);
        return ([.. pages.Select(p => (p.Number, p.Text))], read.Version ?? doc.SourceVersion, next);
    }

    /// <summary>The passages' vectors, through the embedding alias for the document's scope and class; none when no model may embed it.</summary>
    private async Task<(List<float[]>? Vectors, string? Model, string? Problem)> EmbedAsync(Document doc, IReadOnlyList<CutPassage> cut, CancellationToken ct)
    {
        if (cut.Count == 0)
        {
            return (null, null, "There's no text in it to search.");
        }

        List<float[]> vectors = [];
        string? model = null;
        try
        {
            foreach (CutPassage[] batch in cut.Chunk(DocumentsLimits.EmbedBatch))
            {
                AiEmbedding embedded = await ai.EmbedAsync(new AiEmbedRequest(O.EmbedAlias, [.. batch.Select(p => p.ForEmbedding)])
                {
                    InputType = AiEmbedInputTypes.Document,
                    Purpose = DocumentsPurposes.Embed,
                    ScopeId = doc.ScopeId,
                    AccountId = doc.AddedBy,
                    DataClass = doc.DataClass,
                    About = new AiSubject("document", doc.Id.ToString()),
                }, ct);
                if (model is not null && embedded.Model != model)
                {
                    // The alias moved mid-document: start again, so one document's vectors are one model's.
                    throw new AiRefusedException(AiRefusalCodes.Provider, "The embedding model changed while the document was embedded.");
                }

                model = embedded.Model;
                vectors.AddRange(embedded.Vectors);
            }
        }
        catch (AiRefusedException e) when (e.Code is AiRefusalCodes.NoModel or AiRefusalCodes.DataNotAllowed)
        {
            return (null, null, $"Searched by its words only: {e.Message}");
        }

        return (vectors, model, null);
    }

    private async Task SaveAsync(Document doc, Guid stamp, IReadOnlyList<CutPassage> cut, List<float[]>? vectors, string? model, string? problem,
        string? version, CancellationToken ct)
    {
        await documentsDb.RunAsync(async db =>
        {
            int updated = await db.Documents.Where(d => d.Id == doc.Id && d.Stamp == stamp).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, DocumentStatuses.Ready)
                .SetProperty(d => d.Work, DocumentWork.Read)
                .SetProperty(d => d.Passages, cut.Count)
                .SetProperty(d => d.Model, model)
                .SetProperty(d => d.Problem, problem)
                .SetProperty(d => d.RetryAt, (DateTime?)null)
                .SetProperty(d => d.Attempts, 0)
                .SetProperty(d => d.ReadAt, DateTime.UtcNow)
                .SetProperty(d => d.SourceVersion, version)
                .SetProperty(d => d.Stamp, Guid.NewGuid()), ct);
            if (updated == 0)
            {
                throw new DbUpdateConcurrencyException("The document changed while it was read.");
            }

            await db.DocumentPassages.Where(p => p.DocumentId == doc.Id).ExecuteDeleteAsync(ct);
            for (int i = 0; i < cut.Count; i++)
            {
                CutPassage p = cut[i];
                db.DocumentPassages.Add(new DocumentPassage(Guid.NewGuid(), doc.Id, p.Page, p.Ordinal, p.Heading, p.Text)
                {
                    SourceVersion = version,
                    Model = vectors is null ? null : model,
                    Embedding = vectors?[i],
                });
            }

            await db.SaveChangesAsync(ct);
        }, isTransactional: true, ct);
    }

    private async Task FailAsync(Document doc, Guid stamp, string problem, CancellationToken ct)
    {
        if (await EndAsync(doc.Id, stamp, DocumentStatuses.Failed, problem, null, 0, ct))
        {
            await events.OnAsync(new DocumentEvent(DocumentActions.Failed, doc.Id, doc.ScopeId, doc.Name, null), ct);
        }
    }

    private async Task<Guid?> MarkAsync(Guid id, Guid stamp, string status, CancellationToken ct)
    {
        Guid next = Guid.NewGuid();
        int updated = await documentsDb.RunAsync(db => db.Documents.Where(d => d.Id == id && d.Stamp == stamp)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, status).SetProperty(d => d.Stamp, next), ct), cancellationToken: ct);
        return updated == 1 ? next : null;
    }

    private Task<bool> EndAsync(Guid id, Guid stamp, string status, string problem, DateTime? retryAt, int attempts, CancellationToken ct) =>
        documentsDb.RunAsync(async db => await db.Documents.Where(d => d.Id == id && d.Stamp == stamp).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.Status, status)
            .SetProperty(d => d.Problem, problem.Length <= 2000 ? problem : problem[..2000])
            .SetProperty(d => d.RetryAt, retryAt)
            .SetProperty(d => d.Attempts, attempts)
            .SetProperty(d => d.Stamp, Guid.NewGuid()), ct) == 1, cancellationToken: ct);

    private Task<bool> ExistsAsync(Guid id, CancellationToken ct) =>
        documentsDb.RunAsync(db => db.Documents.AnyAsync(d => d.Id == id, ct), cancellationToken: ct);
}
