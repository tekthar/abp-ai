using System.Text;
using AbpAi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NpgsqlTypes;
using Volo.Abp;
using Volo.Abp.DependencyInjection;

namespace AbpDocuments;

/// <summary>What to search for, and where.</summary>
public sealed record SearchInput(string Query)
{
    /// <summary>Only these scopes' documents (still only those the person may read); null: every one they may read.</summary>
    public IReadOnlyList<Guid>? ScopeIds { get; init; }

    /// <summary>With <see cref="ScopeIds"/>, the site's own documents too.</summary>
    public bool IncludeSite { get; init; }

    /// <summary>Only these documents.</summary>
    public IReadOnlyList<Guid>? DocumentIds { get; init; }

    /// <summary>
    /// Only passages of these classes (an agent's tool gives those its model may receive); null: any. The rest that
    /// match the question's words are counted in <see cref="SearchResult.Withheld"/>.
    /// </summary>
    public IReadOnlyList<string>? Classes { get; init; }

    /// <summary>How many passages: 8 by default, 30 at most.</summary>
    public int Take { get; init; } = 8;
}

/// <summary>A passage found: its document, page, heading and text, its class, and how it ranked.</summary>
/// <param name="Score">Its reciprocal-rank score: higher is better.</param>
/// <param name="MeaningRank">Its place by meaning (1 the nearest), or null when it wasn't among the nearest.</param>
/// <param name="WordsRank">Its place by words, or null when its words didn't match.</param>
public sealed record FoundPassage(Guid PassageId, Guid DocumentId, string DocumentName, string Kind, Guid? ScopeId, int Page, string? Heading, string Text,
    string DataClass, double Score, int? MeaningRank, int? WordsRank, double? Distance);

/// <summary>The passages, best first; how many others matched but are of a class not asked for; and what was left out of the search.</summary>
public sealed record SearchResult(IReadOnlyList<FoundPassage> Passages, int Withheld, IReadOnlyList<string> Notes);

/// <summary>
/// Hybrid search over what a person may read. The documents' query (the host's scopes and its filter) is the root of
/// both rankings, so nothing they may not read is ranked: the nearest passages by meaning, the question embedded once for
/// each embedding model among them (by the model's name, so it meets the same model), and the best by their words
/// (<c>ts_rank_cd</c> over the question's words in English, Arabic and as written), merged by reciprocal rank.
/// </summary>
public class DocumentsSearch(DocumentsDb documentsDb, DocumentsService documents, IAi ai, IOptions<DocumentsOptions> options, ILogger<DocumentsSearch> logger)
    : ITransientDependency
{
    /// <summary>Reciprocal rank's constant: a passage first by one way of ranking scores 1/(k+1).</summary>
    public const int RrfK = 60;

    /// <summary>The most embedding models a question is embedded for.</summary>
    public const int MaxModels = 4;

    public async Task<SearchResult> SearchAsync(Guid accountId, SearchInput input, CancellationToken cancellationToken)
    {
        string question = (input.Query ?? "").Trim();
        if (question.Length == 0)
        {
            throw new UserFriendlyException("Give the words to search for.", DocumentsErrorCodes.Invalid);
        }

        if (question.Length > 2000)
        {
            question = question[..2000];
        }

        int take = Math.Clamp(input.Take, 1, DocumentsLimits.MaxTake);
        List<string> notes = [];
        return await documentsDb.RunAsync(async db =>
        {
            IQueryable<Document> readable = (await documents.ReadableAsync(db, accountId, cancellationToken)).Where(d => d.Status == DocumentStatuses.Ready
                || (d.Passages > 0 && (d.Status == DocumentStatuses.Queued || d.Status == DocumentStatuses.Indexing || d.Status == DocumentStatuses.Waiting)));
            if (input.ScopeIds is { } scopes)
            {
                Guid[] ids = [.. scopes];
                bool site = input.IncludeSite;
                readable = readable.Where(d => (d.ScopeId != null && ids.Contains(d.ScopeId.Value)) || (site && d.ScopeId == null));
            }

            if (input.DocumentIds is { } only)
            {
                Guid[] ids = [.. only];
                readable = readable.Where(d => ids.Contains(d.Id));
            }

            IQueryable<Document> asked = readable;
            if (input.Classes is { } classes)
            {
                string[] allowed = [.. classes];
                asked = readable.Where(d => allowed.Contains(d.DataClass));
            }

            var passages = from p in db.DocumentPassages
                           join d in asked on p.DocumentId equals d.Id
                           select p;

            // By meaning: the question embedded for each model the person's passages were embedded with.
            Dictionary<Guid, (int Rank, double Distance)> byMeaning = [];
            List<string?> models = await asked.Where(d => d.Model != null).Select(d => d.Model).Distinct().Take(MaxModels + 1).ToListAsync(cancellationToken);
            if (models.Count > MaxModels)
            {
                notes.Add($"Your documents were embedded with more than {MaxModels} models; some are searched by their words only.");
            }

            List<(Guid Id, double Distance)> near = [];
            foreach (string model in models.Take(MaxModels).OfType<string>())
            {
                float[] q;
                try
                {
                    q = (await ai.EmbedAsync(new AiEmbedRequest(model, [question])
                    {
                        InputType = AiEmbedInputTypes.Query,
                        Purpose = DocumentsPurposes.Search,
                        AccountId = accountId,
                        DataClass = options.Value.QuestionDataClass,
                    }, cancellationToken)).Vectors[0];
                }
                catch (AiRefusedException e)
                {
                    logger.LogInformation("A search left {Model}'s documents to their words: {Reason}", model, e.Message);
                    notes.Add($"Documents embedded with {model} were searched by their words only: {e.Message}");
                    continue;
                }

                near.AddRange((await passages.Where(p => p.Model == model && p.Embedding != null)
                    .OrderBy(p => DocumentsSql.CosineDistance(p.Embedding!, q))
                    .Take(DocumentsLimits.Candidates)
                    .Select(p => new { p.Id, Distance = DocumentsSql.CosineDistance(p.Embedding!, q) })
                    .ToListAsync(cancellationToken)).Select(x => (x.Id, x.Distance)));
            }

            int r = 0;
            foreach ((Guid id, double distance) in near.OrderBy(n => n.Distance).Take(DocumentsLimits.Candidates))
            {
                byMeaning[id] = (++r, distance);
            }

            // By words.
            Dictionary<Guid, int> byWords = [];
            WordsQuery? words = WordsQuery.Of(question);
            if (words is not null)
            {
                List<Guid> matched = await words.Rank(passages)
                    .Take(DocumentsLimits.Candidates)
                    .Select(p => p.Id)
                    .ToListAsync(cancellationToken);
                for (int i = 0; i < matched.Count; i++)
                {
                    byWords[matched[i]] = i + 1;
                }
            }

            // Merged by reciprocal rank.
            List<(Guid Id, double Score)> merged = [.. byMeaning.Keys.Union(byWords.Keys)
                .Select(id => (id, Score: (byMeaning.TryGetValue(id, out var m) ? 1.0 / (RrfK + m.Rank) : 0) + (byWords.TryGetValue(id, out int w) ? 1.0 / (RrfK + w) : 0)))
                .OrderByDescending(x => x.Score)
                .Take(take)];
            Guid[] top = [.. merged.Select(m => m.Id)];
            var rows = await (from p in db.DocumentPassages
                              join d in asked on p.DocumentId equals d.Id
                              where top.Contains(p.Id)
                              select new { p.Id, p.DocumentId, d.Name, d.Kind, d.ScopeId, p.Page, p.Heading, p.Text, d.DataClass })
                .ToDictionaryAsync(x => x.Id, cancellationToken);
            List<FoundPassage> found = [.. merged.Where(m => rows.ContainsKey(m.Id)).Select(m =>
            {
                var x = rows[m.Id];
                bool meant = byMeaning.TryGetValue(m.Id, out var mr);
                return new FoundPassage(x.Id, x.DocumentId, x.Name, x.Kind, x.ScopeId, x.Page, x.Heading, x.Text, x.DataClass, Math.Round(m.Score, 5),
                    meant ? mr.Rank : null, byWords.TryGetValue(m.Id, out int wr) ? wr : null, meant ? Math.Round(mr.Distance, 4) : null);
            })];

            // What matched but wasn't asked for, counted by its words only: nothing of it is read or returned.
            int withheld = 0;
            if (input.Classes is { } given && words is not null)
            {
                string[] allowed = [.. given];
                withheld = await words.Matching(from p in db.DocumentPassages
                                                join d in readable.Where(d => !allowed.Contains(d.DataClass)) on p.DocumentId equals d.Id
                                                select p).Take(100).CountAsync(cancellationToken);
            }

            return new SearchResult(found, withheld, notes);
        }, cancellationToken: cancellationToken);
    }
}

/// <summary>
/// A question as full-text search takes it: its words OR'ed, in English and Arabic (each stemmed its way), and its
/// codes and numbers as written. Only letters and digits are kept, so nothing in it is query syntax.
/// </summary>
internal sealed class WordsQuery
{
    private readonly string _words;
    private readonly string? _codes;

    private WordsQuery(string words, string? codes)
    {
        _words = words;
        _codes = codes;
    }

    public static WordsQuery? Of(string question)
    {
        List<string> words = [.. Words(ArabicText.Normalize(question).ToLowerInvariant()).Distinct().Take(32)];
        if (words.Count == 0)
        {
            return null;
        }

        string[] codes = [.. words.Where(w => w.Any(char.IsDigit))];
        return new WordsQuery(string.Join(" | ", words), codes.Length > 0 ? string.Join(" | ", codes) : null);
    }

    /// <summary>The passages whose words match.</summary>
    public IQueryable<DocumentPassage> Matching(IQueryable<DocumentPassage> passages)
    {
        string words = _words;
        if (_codes is not { } codes)
        {
            return passages.Where(p => p.Search.Matches(EF.Functions.ToTsQuery("english", words).Or(EF.Functions.ToTsQuery("arabic", words))));
        }

        return passages.Where(p => p.Search.Matches(EF.Functions.ToTsQuery("english", words).Or(EF.Functions.ToTsQuery("arabic", words))
            .Or(EF.Functions.ToTsQuery("simple", codes))));
    }

    /// <summary>The passages whose words match, best first (<c>ts_rank_cd</c>).</summary>
    public IQueryable<DocumentPassage> Rank(IQueryable<DocumentPassage> passages)
    {
        string words = _words;
        if (_codes is not { } codes)
        {
            return Matching(passages).OrderByDescending(p => p.Search.RankCoverDensity(EF.Functions.ToTsQuery("english", words).Or(EF.Functions.ToTsQuery("arabic", words))));
        }

        return Matching(passages).OrderByDescending(p => p.Search.RankCoverDensity(EF.Functions.ToTsQuery("english", words).Or(EF.Functions.ToTsQuery("arabic", words))
            .Or(EF.Functions.ToTsQuery("simple", codes))));
    }

    private static IEnumerable<string> Words(string text)
    {
        StringBuilder word = new();
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(c);
            }
            else if (word.Length > 0)
            {
                if (word.Length > 1 || char.IsDigit(word[0]))
                {
                    yield return word.ToString();
                }

                word.Clear();
            }
        }

        if (word.Length > 1 || (word.Length == 1 && char.IsDigit(word[0])))
        {
            yield return word.ToString();
        }
    }

}
