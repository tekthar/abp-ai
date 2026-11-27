using System.Runtime.CompilerServices;
using AbpAi;
using AbpCluster;
using AbpCluster.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Volo.Abp.EntityFrameworkCore.PostgreSql;
using Volo.Abp.Modularity;

namespace AbpDocuments;

/// <summary>
/// Documents people add, read and searched: <c>[DependsOn(typeof(AbpDocumentsModule))]</c>, your context replacing
/// <see cref="IDocumentsDbContext"/> with <c>builder.ConfigureDocuments()</c> in its model (and a migration that makes the
/// <c>vector</c> extension), your <see cref="IDocumentsRights"/>, and AbpAi aliases for reading images
/// (<see cref="DocumentsOptions.ReadAlias"/>) and embedding (<see cref="DocumentsOptions.EmbedAlias"/>). Uploads are kept
/// in AbpCluster.Storage's <c>documents</c> store; every server reads documents in the background, one per document.
/// </summary>
[DependsOn(typeof(AbpEntityFrameworkCorePostgreSqlModule), typeof(AbpAiModule), typeof(AbpClusterModule), typeof(AbpClusterStorageModule))]
public class AbpDocumentsModule : AbpModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context) =>
        PreConfigure<ClusterStorageOptions>(o =>
        {
            if (!o.Stores.Contains(DocumentsStore.Name))
            {
                o.Stores.Add(DocumentsStore.Name);
            }
        });

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        IServiceCollection services = context.Services;
        services.Configure<DocumentsOptions>(services.GetConfiguration().GetSection(DocumentsOptions.Section));
        services.TryAddTransient<IDocumentsRights, NoDocumentsRights>();
        services.TryAddTransient<IDocumentsFilter, NoDocumentsFilter>();
        services.TryAddTransient<IDocumentsEvents, NoDocumentsEvents>();
        services.AddTransient<IStoredFiles, DocumentsStoredFiles>();
        services.AddHostedService<DocumentsWorker>();
    }
}

/// <summary>The library's files, for storage moves and the check after a takeover (AbpCluster.Storage): every upload, with its hash.</summary>
public sealed class DocumentsStoredFiles(IServiceScopeFactory scopes) : IStoredFiles
{
    public async IAsyncEnumerable<StoredFile> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Guid after = Guid.Empty;
        while (true)
        {
            List<(Guid Id, string Sha)> page;
            await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
            {
                page = await scope.ServiceProvider.GetRequiredService<DocumentsDb>().RunAsync(db => db.Documents.AsNoTracking()
                    .Where(d => d.Source == DocumentSources.Library && d.Id.CompareTo(after) > 0)
                    .OrderBy(d => d.Id).Take(1000).Select(d => new ValueTuple<Guid, string>(d.Id, d.Sha256)).ToListAsync(cancellationToken),
                    cancellationToken: cancellationToken);
            }

            foreach ((Guid id, string sha) in page)
            {
                yield return new StoredFile(DocumentsStore.Name, DocumentsStore.BlobOf(id), sha);
            }

            if (page.Count < 1000)
            {
                yield break;
            }

            after = page[^1].Id;
        }
    }
}
