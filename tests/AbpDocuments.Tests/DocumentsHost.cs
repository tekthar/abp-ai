using System.Collections.Concurrent;
using AbpAi;
using AbpAi.Providers;
using AbpCluster.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Volo.Abp;
using Volo.Abp.Autofac;
using Volo.Abp.Data;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Modularity;
using Volo.Abp.Uow;

namespace AbpDocuments.Tests;

/// <summary>The tests' context: the documents' tables and AbpAi's, as a host has them in its own.</summary>
[ConnectionStringName("Default")]
[ReplaceDbContext(typeof(IDocumentsDbContext), typeof(IAiDbContext))]
public sealed class TestDb(DbContextOptions<TestDb> options) : AbpDbContext<TestDb>(options), IDocumentsDbContext, IAiDbContext
{
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentPage> DocumentPages => Set<DocumentPage>();
    public DbSet<DocumentPassage> DocumentPassages => Set<DocumentPassage>();
    public DbSet<AiConnection> AiConnections => Set<AiConnection>();
    public DbSet<AiAlias> AiAliases => Set<AiAlias>();
    public DbSet<AiLimit> AiLimits => Set<AiLimit>();
    public DbSet<AiUsageRecord> AiUsage => Set<AiUsageRecord>();
    public DbSet<AiPromptRecord> AiPrompts => Set<AiPromptRecord>();
    public DbSet<AiSite> AiSites => Set<AiSite>();

    /// <summary>A host's own table its filter reads: records some documents are on, some of them restricted.</summary>
    public DbSet<TestRecord> Records => Set<TestRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ConfigureAi();
        modelBuilder.ConfigureDocuments();
        modelBuilder.Entity<TestRecord>(b =>
        {
            b.ToTable("records");
            b.HasKey(r => r.Id);
            b.Property(r => r.Id).ValueGeneratedNever();
        });
    }
}

public sealed class TestRecord
{
    public string Id { get; set; } = "";

    /// <summary>Only <see cref="Reader"/> may read a restricted record's documents.</summary>
    public bool Restricted { get; set; }

    public Guid? Reader { get; set; }
}

/// <summary>Who reads, adds and manages where, set by each test.</summary>
public sealed class TestRights : IDocumentsRights
{
    public ConcurrentDictionary<Guid, DocumentsReach> Readable { get; } = new();

    public ConcurrentDictionary<(Guid, Guid?), bool> Adds { get; } = new();

    public ConcurrentDictionary<(Guid, Guid?), bool> Manages { get; } = new();

    public Task<DocumentsReach> ReadableAsync(Guid accountId, CancellationToken cancellationToken) =>
        Task.FromResult(Readable.TryGetValue(accountId, out DocumentsReach? reach) ? reach : DocumentsReach.None);

    public Task<bool> MayAddAsync(Guid accountId, Guid? scopeId, CancellationToken cancellationToken) =>
        Task.FromResult(Adds.TryGetValue((accountId, scopeId), out bool may) && may);

    public Task<bool> MayManageAsync(Guid accountId, Guid? scopeId, CancellationToken cancellationToken) =>
        Task.FromResult(Manages.TryGetValue((accountId, scopeId), out bool may) && may);
}

/// <summary>The host's rule beyond scopes: a document on a restricted record is only its reader's, as an EXISTS against the host's table.</summary>
public sealed class RestrictedRecordsFilter : IDocumentsFilter
{
    public Task<IQueryable<Document>> FilterAsync(IQueryable<Document> documents, IDocumentsDbContext db, Guid accountId, CancellationToken cancellationToken)
    {
        IQueryable<TestRecord> records = ((TestDb)db).Records;
        return Task.FromResult(documents.Where(d => d.OwnerKind != "record"
            || !records.Any(r => r.Id == d.OwnerId && r.Restricted && r.Reader != accountId)));
    }
}

/// <summary>Everyone is the AI gateway's admin, so tests set connections and aliases.</summary>
public sealed class EveryoneAdmins : IAiRights
{
    public Task<bool> IsSiteAdminAsync(AiActor actor, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<bool> AdministersScopeAsync(AiActor actor, Guid scopeId, CancellationToken cancellationToken) => Task.FromResult(true);
}

/// <summary>A provider whose vectors are as long as its model's name says (<c>vec-8</c>), the demo model's words-in-places vectors.</summary>
public sealed class SizedEmbedder : IAiProvider
{
    public const string TheKind = "sized";

    public string Kind => TheKind;

    public bool CanEmbed => true;

    public Task<AiProviderEmbedding> EmbedAsync(AiEmbedCall call, CancellationToken cancellationToken)
    {
        int size = int.Parse(call.Target.Model[(call.Target.Model.IndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        return Task.FromResult(new AiProviderEmbedding([.. call.Inputs.Select(t => DemoProvider.Vector(t, size))], call.Inputs.Sum(t => t.Length / 4 + 1)));
    }

    public Task<AiProviderReply> CompleteAsync(AiProviderCall call, CancellationToken cancellationToken) => throw new NotSupportedException();

    public IAsyncEnumerable<AiStreamEvent> StreamAsync(AiProviderCall call, CancellationToken cancellationToken) => throw new NotSupportedException();
}

[DependsOn(typeof(AbpAutofacModule), typeof(HttpApi.AbpDocumentsHttpApiModule))]
public sealed class DocumentsTestModule : AbpModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context) =>
        PreConfigure<ClusterStorageOptions>(o => o.FileSystemPath = context.Services.GetConfiguration()["Tests:Files"]);

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<TestDb>(o =>
        {
            o.ReplaceDbContext<IDocumentsDbContext>();
            o.ReplaceDbContext<IAiDbContext>();
        });
        Configure<AbpDbContextOptions>(o => o.UseNpgsql());
        context.Services.AddSingleton<TestRights>();
        context.Services.Replace(ServiceDescriptor.Transient<IDocumentsRights>(sp => sp.GetRequiredService<TestRights>()));
        context.Services.Replace(ServiceDescriptor.Transient<IDocumentsFilter, RestrictedRecordsFilter>());
        context.Services.Replace(ServiceDescriptor.Singleton<IAiRights, EveryoneAdmins>());
        context.Services.AddTransient<IAiProvider, SizedEmbedder>();
        context.Services.AddSingleton<SourceFiles>();
        context.Services.AddSingleton<IDocumentSource>(sp => sp.GetRequiredService<SourceFiles>());
        Configure<DocumentsOptions>(o =>
        {
            o.Worker = false; // the tests run the worker's rounds themselves
            o.DataClasses = [AiDataClasses.Public, AiDataClasses.Internal, AiDataClasses.Sensitive, "restricted"];
        });
        Configure<AiOptions>(o => o.DataClasses = [.. AiDataClasses.Default, "restricted"]);
    }
}

/// <summary>
/// An app of the package on a PostgreSQL database of its own (with pgvector), its files in a folder of their own:
/// <c>ABP_AI_TEST_POSTGRES</c> names the server, a local pgvector container by default.
/// </summary>
public sealed class DocumentsHost : IAsyncLifetime
{
    private readonly string _database = $"abp_documents_{Guid.NewGuid():N}";
    private readonly string _files = Path.Combine(Path.GetTempPath(), $"abp-documents-{Guid.NewGuid():N}");
    private string _server = "";
    private IAbpApplicationWithInternalServiceProvider? _app;

    public static readonly AiActor Admin = new(Guid.Parse("00000000-0000-0000-0000-00000000a001"));

    public IServiceProvider Services => _app!.ServiceProvider;

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public TestRights Rights => Get<TestRights>();

    public async Task InitializeAsync()
    {
        _server = Environment.GetEnvironmentVariable("ABP_AI_TEST_POSTGRES") is { Length: > 0 } s ? s : "Host=localhost;Port=5432;Username=postgres;Password=postgres";
        string connection = new NpgsqlConnectionStringBuilder(_server) { Database = _database }.ConnectionString;
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = connection,
            ["Tests:Files"] = _files,
        }).Build();
        _app = await AbpApplicationFactory.CreateAsync<DocumentsTestModule>(o =>
        {
            o.UseAutofac();
            o.Services.ReplaceConfiguration(configuration);
        });
        await _app.InitializeAsync();
        await InDbAsync(db => db.Database.EnsureCreatedAsync());
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.ShutdownAsync();
            _app.Dispose();
        }

        NpgsqlConnection.ClearAllPools();
        await using NpgsqlConnection admin = new(_server);
        await admin.OpenAsync();
        await using NpgsqlCommand drop = new($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
        if (Directory.Exists(_files))
        {
            Directory.Delete(_files, recursive: true);
        }
    }

    public async Task<T> InDbAsync<T>(Func<TestDb, Task<T>> work)
    {
        using IServiceScope scope = Services.CreateScope();
        using IUnitOfWork uow = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);
        TestDb db = await scope.ServiceProvider.GetRequiredService<IDbContextProvider<TestDb>>().GetDbContextAsync();
        T result = await work(db);
        await uow.CompleteAsync();
        return result;
    }

    public Task InDbAsync(Func<TestDb, Task> work) => InDbAsync<bool>(async db => { await work(db); return true; });

    /// <summary>A demo connection with a model that reads images and one that embeds, and the aliases the package uses.</summary>
    public async Task<AiConnectionInfo> UseDemoModelsAsync(IReadOnlyList<string>? classes = null)
    {
        AiAdminService admin = Get<AiAdminService>();
        AiConnectionInfo demo = await admin.CreateConnectionAsync(Admin, null, new AiConnectionInput("demo", "demo", ["reader", "embedder"],
            classes ?? [.. AiDataClasses.Default, "restricted"])
        {
            ModelSettings = new Dictionary<string, AiModelSettings> { ["reader"] = new() { Images = true }, ["embedder"] = new() { Embeds = true } },
        }, default);
        await admin.SetAliasAsync(Admin, null, "read", [new AiAliasTarget(demo.Id, "reader")], default);
        await admin.SetAliasAsync(Admin, null, "embeddings", [new AiAliasTarget(demo.Id, "embedder")], default);
        return demo;
    }

    /// <summary>Every document due is read, as the worker's rounds would.</summary>
    public async Task ReadAllAsync()
    {
        DocumentsWorker worker = ActivatorUtilities.CreateInstance<DocumentsWorker>(Services);
        for (int i = 0; i < 10 && await worker.RoundAsync(default); i++)
        {
        }
    }

    public async Task<DocumentInfo> AddAsync(Guid by, Guid? scope, string name, byte[] bytes, string? dataClass = null)
    {
        Rights.Adds[(by, scope)] = true;
        using MemoryStream content = new(bytes);
        return await Get<DocumentsService>().AddAsync(by, scope, name, dataClass, content, default);
    }
}
