using AbpAi;
using AbpCluster;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AbpDocuments;

/// <summary>
/// Every server's worker: each round, the documents to read (queued, or waiting whose time has come, or left mid-way by
/// a server that stopped), each under its cluster lock (<c>documents:{id}</c>), so one server reads a document and the
/// others skip it; and now and then, under <c>documents:models</c>, whether the embedding alias leads to another model,
/// which embeds those documents again. Woken at once by an upload on this server.
/// </summary>
public sealed class DocumentsWorker(IServiceScopeFactory scopes, DocumentsSignal signal, IClusterLocks locks, IOptions<DocumentsOptions> options,
    ILogger<DocumentsWorker> logger) : BackgroundService
{
    public const string ModelsLock = "documents:models";

    public static string LockOf(Guid id) => $"documents:{id:N}";

    private DateTime _modelsCheckedAt = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Worker)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool more = await RoundAsync(stoppingToken);
                if (DateTime.UtcNow - _modelsCheckedAt >= options.Value.ModelCheckEvery)
                {
                    _modelsCheckedAt = DateTime.UtcNow;
                    await CheckModelsAsync(stoppingToken);
                }

                if (!more)
                {
                    await signal.WaitAsync(options.Value.PollEvery, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // A database that's away for a moment: the next round tries again.
                logger.LogWarning(e, "The documents' worker's round failed; it tries again.");
                await Task.Delay(options.Value.PollEvery, stoppingToken);
            }
        }
    }

    /// <summary>One round: reads the documents due, each under its lock. True when there may be more due now.</summary>
    public async Task<bool> RoundAsync(CancellationToken cancellationToken)
    {
        List<Guid> due;
        await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
        {
            DateTime now = DateTime.UtcNow;
            due = await scope.ServiceProvider.GetRequiredService<DocumentsDb>().RunAsync(db => db.Documents.AsNoTracking()
                .Where(d => DocumentStatuses.Open.Contains(d.Status) || (d.Status == DocumentStatuses.Waiting && d.RetryAt <= now))
                .OrderBy(d => d.AddedAt)
                .Select(d => d.Id)
                .Take(20)
                .ToListAsync(cancellationToken), cancellationToken: cancellationToken);
        }

        int ran = 0;
        foreach (Guid id in due)
        {
            await using IAsyncDisposable? held = await locks.TryTakeAsync(LockOf(id), cancellationToken);
            if (held is null)
            {
                continue; // Another server reads it.
            }

            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<DocumentsIndexer>().RunAsync(id, cancellationToken))
            {
                ran++;
            }
        }

        return ran > 0 && due.Count == 20;
    }

    /// <summary>
    /// Where the embedding alias leads now, for each scope and class among ready documents: those whose passages came
    /// from another model (or from none, when one may embed them now) are embedded again from their kept text.
    /// </summary>
    public async Task CheckModelsAsync(CancellationToken cancellationToken)
    {
        await using IAsyncDisposable? held = await locks.TryTakeAsync(ModelsLock, cancellationToken);
        if (held is null)
        {
            return;
        }

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        DocumentsDb db = scope.ServiceProvider.GetRequiredService<DocumentsDb>();
        IAi ai = scope.ServiceProvider.GetRequiredService<IAi>();
        var groups = await db.RunAsync(d => d.Documents.AsNoTracking().Where(x => x.Status == DocumentStatuses.Ready && x.Passages > 0)
            .GroupBy(x => new { x.ScopeId, x.DataClass, x.Model }).Select(g => g.Key).ToListAsync(cancellationToken), cancellationToken: cancellationToken);
        int queued = 0;
        foreach (var group in groups)
        {
            string model;
            try
            {
                model = (await ai.ResolveAsync(options.Value.EmbedAlias, group.ScopeId, group.DataClass, cancellationToken)).Model;
            }
            catch (AiRefusedException)
            {
                continue; // No model may embed it now: it stays as it is.
            }

            if (model == group.Model)
            {
                continue;
            }

            queued += await db.RunAsync(d => d.Documents.Where(x => x.Status == DocumentStatuses.Ready && x.Passages > 0
                    && x.ScopeId == group.ScopeId && x.DataClass == group.DataClass && x.Model == group.Model)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DocumentStatuses.Queued).SetProperty(x => x.Work, DocumentWork.Embed)
                    .SetProperty(x => x.Stamp, Guid.NewGuid()), cancellationToken), cancellationToken: cancellationToken);
        }

        if (queued > 0)
        {
            logger.LogInformation("The embedding model changed: {Count} documents are embedded again.", queued);
            signal.Notify();
        }
    }
}
