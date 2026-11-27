using System.Text;

namespace AbpDocuments.Reading;

/// <summary>A passage as cut: its page, its place, the heading it sits under, and its text.</summary>
public sealed record CutPassage(int Page, int Ordinal, string? Heading, string Text)
{
    /// <summary>What's embedded: the heading, then the text, so a passage keeps what it's about.</summary>
    public string ForEmbedding => Heading is { Length: > 0 } h ? $"{h}\n\n{Text}" : Text;
}

/// <summary>
/// Cuts pages into passages: within a page at paragraph ends (a long paragraph at sentence ends, else at spaces), about
/// <c>length</c> characters each, the next starting with the last <c>overlap</c> characters of the one before. A passage
/// never spans pages, and each keeps the heading above it, which carries on to the pages after.
/// </summary>
public static class Passages
{
    public static IReadOnlyList<CutPassage> Cut(IEnumerable<(int Number, string Text)> pages, int length, int overlap)
    {
        length = Math.Max(200, length);
        overlap = Math.Clamp(overlap, 0, length / 3);
        List<CutPassage> result = [];
        string? heading = null;
        foreach ((int number, string text) in pages)
        {
            StringBuilder current = new();
            string? currentHeading = heading;
            void Flush(bool keepOverlap)
            {
                string done = current.ToString().Trim();
                if (done.Length > 0)
                {
                    result.Add(new CutPassage(number, result.Count, currentHeading, done));
                }

                current.Clear();
                if (keepOverlap && overlap > 0 && done.Length > overlap)
                {
                    current.Append(Tail(done, overlap)).Append(' ');
                }

                currentHeading = heading;
            }

            foreach (string paragraph in Paragraphs(text))
            {
                if (HeadingOf(paragraph) is { } h)
                {
                    // A heading starts a passage of its own section.
                    Flush(keepOverlap: false);
                    heading = h;
                    currentHeading = h;
                    continue;
                }

                foreach (string piece in Pieces(paragraph, length))
                {
                    if (current.Length > 0 && current.Length + piece.Length + 2 > length)
                    {
                        Flush(keepOverlap: true);
                    }

                    if (current.Length > 0)
                    {
                        current.Append("\n\n");
                    }

                    current.Append(piece);
                }
            }

            Flush(keepOverlap: false);
        }

        return result;
    }

    private static IEnumerable<string> Paragraphs(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(p => p.Split('\n').Any(l => HeadingOf(l) is not null) ? SplitHeadings(p) : [p]);

    // A heading line inside a block (Markdown writes them without blank lines) is a paragraph of its own.
    private static IEnumerable<string> SplitHeadings(string block)
    {
        StringBuilder rest = new();
        foreach (string line in block.Split('\n'))
        {
            if (HeadingOf(line) is not null)
            {
                if (rest.Length > 0)
                {
                    yield return rest.ToString().Trim();
                    rest.Clear();
                }

                yield return line.Trim();
            }
            else
            {
                rest.Append(line).Append('\n');
            }
        }

        if (rest.ToString().Trim() is { Length: > 0 } last)
        {
            yield return last;
        }
    }

    /// <summary>A Markdown heading's words (<c># Terms</c> is <c>Terms</c>), or null.</summary>
    internal static string? HeadingOf(string paragraph)
    {
        string line = paragraph.Trim();
        if (line.Contains('\n', StringComparison.Ordinal) || !line.StartsWith('#'))
        {
            return null;
        }

        string words = line.TrimStart('#');
        return words.Length > 0 && words[0] == ' ' && line.Length - words.Length <= 6 ? Truncate(words.Trim(), 500) : null;
    }

    /// <summary>A paragraph in pieces no longer than <paramref name="length"/>: at sentence ends, else at spaces, else anywhere.</summary>
    private static IEnumerable<string> Pieces(string paragraph, int length)
    {
        if (paragraph.Length <= length)
        {
            yield return paragraph;
            yield break;
        }

        int start = 0;
        while (start < paragraph.Length)
        {
            int end = Math.Min(paragraph.Length, start + length);
            if (end < paragraph.Length)
            {
                int cut = LastSentenceEnd(paragraph, start + length / 2, end);
                if (cut < 0)
                {
                    cut = paragraph.LastIndexOf(' ', end - 1, end - start - length / 2);
                }

                end = cut > start ? cut + 1 : end;
            }

            yield return paragraph[start..end].Trim();
            start = end;
        }
    }

    private static int LastSentenceEnd(string text, int from, int to)
    {
        for (int i = to - 1; i >= from; i--)
        {
            if (text[i] is '.' or '!' or '?' or '؟' or '۔' or '\n' && (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Tail(string text, int length)
    {
        string tail = text[^Math.Min(length, text.Length)..];
        int space = tail.IndexOf(' ', StringComparison.Ordinal);
        return space >= 0 && space < tail.Length - 1 ? tail[(space + 1)..] : tail;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
