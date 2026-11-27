using System.Data;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Uow;

namespace AbpDocuments;

/// <summary>
/// The package's tables, as the host has them in its own context: the host's migrations make them (and the
/// <c>vector</c> extension), and its context says it replaces this one (<c>[ReplaceDbContext(typeof(IDocumentsDbContext))]</c>).
/// A host's <see cref="IDocumentsFilter"/> gets this same context, so it reaches its own tables in the same query.
/// </summary>
[ConnectionStringName("Default")]
public interface IDocumentsDbContext : IEfCoreDbContext
{
    DbSet<Document> Documents { get; }

    DbSet<DocumentPage> DocumentPages { get; }

    DbSet<DocumentPassage> DocumentPassages { get; }
}

/// <summary>The tables' names, for a host that wants its own.</summary>
public sealed record DocumentsTables(string Documents = "documents", string Pages = "pages", string Passages = "passages");

/// <summary>The package's model: <c>builder.ConfigureDocuments()</c> in a host context's <c>OnModelCreating</c>.</summary>
public static class DocumentsModel
{
    /// <summary>The package's schema, apart from any host's, unless the host names its own.</summary>
    public const string Schema = "documents";

    public static void ConfigureDocuments(this ModelBuilder builder, string? schema = Schema, DocumentsTables? tables = null)
    {
        tables ??= new DocumentsTables();
        // pgvector, for comparing vectors (cosine_distance): the host's migration makes it before the tables.
        builder.HasPostgresExtension("vector");

        builder.Entity<Document>(b =>
        {
            b.ToTable(tables.Documents, schema);
            b.HasKey(d => d.Id);
            b.Property(d => d.Id).ValueGeneratedNever();
            b.HasIndex(d => new { d.Source, d.SourceKey }).IsUnique();
            b.HasIndex(d => new { d.ScopeId, d.AddedAt });
            b.HasIndex(d => d.Status);
            b.HasIndex(d => d.AddedBy);
            b.Property(d => d.Source).HasMaxLength(64).IsRequired();
            b.Property(d => d.SourceKey).HasMaxLength(256).IsRequired();
            b.Property(d => d.SourceVersion).HasMaxLength(128);
            b.Property(d => d.OwnerKind).HasMaxLength(64);
            b.Property(d => d.OwnerId).HasMaxLength(256);
            b.Property(d => d.Name).HasMaxLength(DocumentsLimits.NameLength).IsRequired();
            b.Property(d => d.Kind).HasMaxLength(16).IsRequired();
            b.Property(d => d.Sha256).HasMaxLength(64).IsRequired();
            b.Property(d => d.DataClass).HasMaxLength(64).IsRequired();
            b.Property(d => d.Status).HasMaxLength(16).IsRequired();
            b.Property(d => d.Work).HasMaxLength(16).IsRequired();
            b.Property(d => d.Problem).HasMaxLength(2000);
            b.Property(d => d.Model).HasMaxLength(256);
            b.Property(d => d.Stamp).IsConcurrencyToken();
        });

        builder.Entity<DocumentPage>(b =>
        {
            b.ToTable(tables.Pages, schema);
            b.HasKey(p => new { p.DocumentId, p.Number });
            b.Property(p => p.Text).IsRequired();
            b.HasOne<Document>().WithMany().HasForeignKey(p => p.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DocumentPassage>(b =>
        {
            b.ToTable(tables.Passages, schema);
            b.HasKey(p => p.Id);
            b.Property(p => p.Id).ValueGeneratedNever();
            b.HasIndex(p => new { p.DocumentId, p.Ordinal });
            b.HasIndex(p => p.Model);
            // Named here, whatever the host's naming, since the generated search column reads them.
            b.Property(p => p.Heading).HasColumnName("Heading").HasMaxLength(500);
            b.Property(p => p.Text).HasColumnName("Text").IsRequired();
            b.Property(p => p.SourceVersion).HasMaxLength(128);
            b.Property(p => p.Model).HasMaxLength(256);
            b.Property(p => p.Embedding).HasColumnType("real[]");
            b.Property(p => p.Search).HasColumnType("tsvector").HasComputedColumnSql(SearchSql, stored: true);
            b.HasIndex(p => p.Search).HasMethod("GIN");
            b.HasOne<Document>().WithMany().HasForeignKey(p => p.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.HasDbFunction(typeof(DocumentsSql).GetMethod(nameof(DocumentsSql.CosineDistance))!)
            .HasTranslation(args => new SqlFunctionExpression(
                "cosine_distance",
                [AsVector(args[0]), AsVector(args[1])],
                nullable: true,
                argumentsPropagateNullability: [true, true],
                typeof(double),
                new DoubleTypeMapping("double precision", DbType.Double)));
    }

    /// <summary>
    /// A passage's words: its heading (weighted A) and its text, each in the <c>simple</c> (codes, numbers, names),
    /// <c>english</c> and <c>arabic</c> configurations, Arabic made one form (<see cref="ArabicText.Sql"/>) first.
    /// </summary>
    internal static readonly string SearchSql =
        "setweight(" + Vectors("coalesce(\"Heading\", '')") + ", 'A') || " + Vectors("\"Text\"");

    private static string Vectors(string column)
    {
        string plain = ArabicText.Sql(column);
        return $"(to_tsvector('simple'::regconfig, {plain}) || to_tsvector('english'::regconfig, {column}) || to_tsvector('arabic'::regconfig, {plain}))";
    }

    private static SqlExpression AsVector(SqlExpression operand) =>
        new SqlUnaryExpression(ExpressionType.Convert, operand, typeof(float[]), VectorCast.Instance);

    /// <summary>The type a <c>real[]</c> is compared as: pgvector's <c>vector</c>, for a cast in SQL only.</summary>
    private sealed class VectorCast : RelationalTypeMapping
    {
        public static readonly VectorCast Instance = new();

        private VectorCast()
            : base("vector", typeof(float[]))
        {
        }

        private VectorCast(RelationalTypeMappingParameters parameters)
            : base(parameters)
        {
        }

        protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters) => new VectorCast(parameters);
    }
}

/// <summary>Functions that run in the database, in a query's LINQ.</summary>
public static class DocumentsSql
{
    /// <summary>pgvector's cosine distance between two vectors kept as <c>real[]</c> (0 the same, 2 opposite). Both must be one size.</summary>
    public static double CosineDistance(float[] a, float[] b) =>
        throw new InvalidOperationException("CosineDistance runs in the database, in a query.");
}

/// <summary>Arabic in one form for search: no diacritics or tatweel, one alef, yaa for alef maqsura, haa for taa marbuta.</summary>
public static class ArabicText
{
    // Fathatan to sukun, the small marks after them, superscript alef, and tatweel.
    private const string Marks = "ً-ٰٟـ";
    private const string From = "أإآٱىة";
    private const string To = "اااايه";

    /// <summary>The text in one form, as the database makes it for search.</summary>
    public static string Normalize(string text)
    {
        System.Text.StringBuilder sb = new(text.Length);
        foreach (char c in text.Normalize(System.Text.NormalizationForm.FormKC))
        {
            if (c is >= 'ً' and <= 'ٟ' or 'ٰ' or 'ـ')
            {
                continue;
            }

            int i = From.IndexOf(c, StringComparison.Ordinal);
            sb.Append(i >= 0 ? To[i] : c);
        }

        return sb.ToString();
    }

    /// <summary>The same in SQL, for a column (immutable, so a generated column may use it).</summary>
    internal static string Sql(string column) => $"translate(regexp_replace({column}, '[{Marks}]', '', 'g'), '{From}', '{To}')";
}

/// <summary>Runs the package's work in a unit of work of its own, so nothing it does waits on a caller's transaction.</summary>
public class DocumentsDb(IUnitOfWorkManager unitOfWorkManager, IDbContextProvider<IDocumentsDbContext> provider) : ITransientDependency
{
#pragma warning disable CA1068
    public async Task<T> RunAsync<T>(Func<IDocumentsDbContext, Task<T>> work, bool isTransactional = false, CancellationToken cancellationToken = default)
    {
        using IUnitOfWork uow = unitOfWorkManager.Begin(requiresNew: true, isTransactional: isTransactional);
        IDocumentsDbContext db = await provider.GetDbContextAsync();
        T result = await work(db);
        await uow.CompleteAsync(cancellationToken);
        return result;
    }

    public Task RunAsync(Func<IDocumentsDbContext, Task> work, bool isTransactional = false, CancellationToken cancellationToken = default) =>
        RunAsync<bool>(async db => { await work(db); return true; }, isTransactional, cancellationToken);
#pragma warning restore CA1068
}
