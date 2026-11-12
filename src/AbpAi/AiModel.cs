using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Uow;

namespace AbpAi;

/// <summary>
/// The gateway's tables, as a host has them in its own context: the host's migrations make them, and its context says
/// it replaces this one (<c>[ReplaceDbContext(typeof(IAiDbContext))]</c>).
/// </summary>
[ConnectionStringName("Default")]
public interface IAiDbContext : IEfCoreDbContext
{
    DbSet<AiConnection> AiConnections { get; }
    DbSet<AiAlias> AiAliases { get; }
    DbSet<AiLimit> AiLimits { get; }
    DbSet<AiUsageRecord> AiUsage { get; }
    DbSet<AiPromptRecord> AiPrompts { get; }
    DbSet<AiSite> AiSites { get; }

    /// <summary>Which of the site's models each scope may use, when the site lists them. A host's context needn't name it.</summary>
    DbSet<AiScopeAccess> AiScopeAccess => Set<AiScopeAccess>();

    /// <summary>Exports of calls for support, kept a week. A host's context needn't name it.</summary>
    DbSet<AiExport> AiExports => Set<AiExport>();
}

/// <summary>Runs the gateway's work in a unit of work: the caller's when there is one, or a new one.</summary>
public class AiDb(IUnitOfWorkManager unitOfWorkManager, IDbContextProvider<IAiDbContext> provider) : ITransientDependency
{
#pragma warning disable CA1068
    public async Task<T> RunAsync<T>(Func<IAiDbContext, Task<T>> work, bool requiresNew = false, CancellationToken cancellationToken = default,
        bool isTransactional = false)
    {
        using IUnitOfWork uow = unitOfWorkManager.Begin(requiresNew: requiresNew, isTransactional: isTransactional);
        IAiDbContext db = await provider.GetDbContextAsync();
        T result = await work(db);
        await uow.CompleteAsync(cancellationToken);
        return result;
    }

    public Task RunAsync(Func<IAiDbContext, Task> work, bool requiresNew = false, CancellationToken cancellationToken = default, bool isTransactional = false) =>
        RunAsync<bool>(async db => { await work(db); return true; }, requiresNew, cancellationToken, isTransactional);
#pragma warning restore CA1068
}

/// <summary>The gateway's model: <c>builder.ConfigureAi()</c> in a host context's <c>OnModelCreating</c>.</summary>
public static class AiModel
{
    /// <summary>The gateway's schema, apart from any host's.</summary>
    public const string Schema = "ai";

    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static void ConfigureAi(this ModelBuilder builder)
    {
        builder.Entity<AiConnection>(b =>
        {
            b.ToTable("connections", Schema);
            // Named apart from a host's own connections table's key.
            b.HasKey(x => x.Id).HasName("PK_ai_connections");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.Provider).HasMaxLength(64).IsRequired();
            b.Property(x => x.Endpoint).HasMaxLength(500).IsRequired();
            b.Property(x => x.Models).HasJsonConversion();
            b.Property(x => x.ModelSettings).HasJsonConversion().HasDefaultValueSql("'{}'::jsonb");
            b.Property(x => x.DataClasses).HasJsonConversion();
            b.Property(x => x.Labels).HasJsonConversion();
            b.Property(x => x.Settings).HasJsonConversion();
            b.Property(x => x.ModelPrices).HasJsonConversion();
            b.Property(x => x.InputPrice).HasPrecision(18, 6);
            b.Property(x => x.OutputPrice).HasPrecision(18, 6);
            b.Property(x => x.CacheWritePrice).HasPrecision(18, 6);
            b.Property(x => x.CacheReadPrice).HasPrecision(18, 6);
            b.HasIndex(x => new { x.ScopeId, x.Name }).IsUnique().AreNullsDistinct(false);
        });

        builder.Entity<AiAlias>(b =>
        {
            b.ToTable("aliases", Schema);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.Targets).HasJsonConversion();
            b.HasIndex(x => new { x.ScopeId, x.Name }).IsUnique().AreNullsDistinct(false);
        });

        builder.Entity<AiLimit>(b =>
        {
            b.ToTable("limits", Schema);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            b.Property(x => x.Period).HasMaxLength(16).IsRequired();
            b.Property(x => x.Measure).HasMaxLength(16).IsRequired().HasDefaultValue(AiLimitMeasures.Cost);
            b.Property(x => x.Amount).HasPrecision(18, 6);
            b.HasIndex(x => new { x.ScopeId, x.Kind, x.Measure, x.Period, x.AccountId, x.GroupId }).IsUnique().AreNullsDistinct(false);
        });

        builder.Entity<AiUsageRecord>(b =>
        {
            b.ToTable("usage", Schema);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.State).HasMaxLength(16).IsRequired();
            b.Property(x => x.ScopePath).HasColumnType("uuid[]").IsRequired();
            b.Property(x => x.Purpose).HasMaxLength(200).IsRequired();
            b.Property(x => x.AboutKind).HasMaxLength(128);
            b.Property(x => x.AboutId).HasMaxLength(128);
            b.Property(x => x.Alias).HasMaxLength(100).IsRequired();
            b.Property(x => x.Connection).HasMaxLength(100).IsRequired();
            b.Property(x => x.Provider).HasMaxLength(64).IsRequired();
            b.Property(x => x.Model).HasMaxLength(200).IsRequired();
            b.Property(x => x.DataClass).HasMaxLength(64).IsRequired();
            b.Property(x => x.Reserved).HasPrecision(18, 6);
            b.Property(x => x.ReservedTokens).HasDefaultValue(0);
            b.Property(x => x.Cost).HasPrecision(18, 6);
            b.Property(x => x.Error).HasMaxLength(1000);
            b.Property(x => x.Code).HasMaxLength(32);
            b.Property(x => x.CacheWriteTokens).HasDefaultValue(0);
            b.Property(x => x.CacheReadTokens).HasDefaultValue(0);
            b.HasIndex(x => x.At);
            b.HasIndex(x => new { x.AccountId, x.At });
            b.HasIndex(x => new { x.OnBehalfOf, x.At });
            b.HasIndex(x => x.ScopePath).HasMethod("gin");
            b.HasIndex(x => new { x.State, x.At });
        });

        builder.Entity<AiPromptRecord>(b =>
        {
            b.ToTable("prompts", Schema);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Messages).HasColumnType("jsonb").IsRequired();
            b.Property(x => x.Reply).HasColumnType("jsonb");
            b.HasIndex(x => x.At);
        });

        builder.Entity<AiSite>(b =>
        {
            b.ToTable("site", Schema);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ScopeDataClasses).HasJsonConversion();
            b.Property(x => x.ScopeAccess).HasMaxLength(16).IsRequired().HasDefaultValue(AiScopeAccessModes.Open);
        });

        builder.Entity<AiScopeAccess>(b =>
        {
            b.ToTable("scope_access", Schema);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.SiteModels).HasJsonConversion();
        });

        builder.Entity<AiExport>(b =>
        {
            b.ToTable("exports", Schema);
            b.HasKey(x => x.Id).HasName("PK_ai_exports");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Content).IsRequired();
            b.HasIndex(x => x.ExpiresAt).HasDatabaseName("IX_ai_exports_ExpiresAt");
        });
    }

    /// <summary>
    /// Leaves the gateway's tables out of a model: for a host's own tests on a database that isn't PostgreSQL (SQLite,
    /// the in-memory one), where its columns (<c>uuid[]</c>, <c>jsonb</c>) can't be made. The gateway itself needs
    /// PostgreSQL; tests that call it run on one.
    /// </summary>
    public static void IgnoreAi(this ModelBuilder builder)
    {
        builder.Ignore<AiConnection>();
        builder.Ignore<AiAlias>();
        builder.Ignore<AiLimit>();
        builder.Ignore<AiUsageRecord>();
        builder.Ignore<AiPromptRecord>();
        builder.Ignore<AiSite>();
        builder.Ignore<AiScopeAccess>();
        builder.Ignore<AiExport>();
    }

    /// <summary>Stores the value as jsonb, compared by its JSON so changes inside it are detected.</summary>
    private static PropertyBuilder<T> HasJsonConversion<T>(this PropertyBuilder<T> property) where T : class?
    {
        property.HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<T>(v, Json)!,
                new ValueComparer<T>(
                    (a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
                    v => JsonSerializer.Serialize(v, Json).GetHashCode(),
                    v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Json), Json)!))
            .HasColumnType("jsonb")
            .IsRequired();
        return property;
    }
}
