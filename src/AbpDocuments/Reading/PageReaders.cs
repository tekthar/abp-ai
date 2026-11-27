using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using PdfPage = UglyToad.PdfPig.Content.Page;
using PdfWord = UglyToad.PdfPig.Content.Word;

namespace AbpDocuments.Reading;

/// <summary>
/// A page as a file gives it: its text (headings as Markdown's <c>#</c> lines), or, for a scanned page or a picture,
/// the image a model must read. A page with neither is empty.
/// </summary>
public sealed record SourcePage(int Number, string Text, byte[]? Image = null, string? ImageType = null)
{
    public bool NeedsModel => Image is not null;
}

/// <summary>A file that can't be read: damaged, protected, or over a limit. The document fails with the message.</summary>
public sealed class DocumentUnreadableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Takes the pages out of a file of a kind, without models: the text a file holds, and the images a model must read.</summary>
public static class PageReaders
{
    /// <summary>Under this many letters, a PDF page with an image is a scanned page.</summary>
    internal const int ScannedBelow = 20;

    public static IReadOnlyList<SourcePage> Read(DocumentKind kind, byte[] bytes, int maxPages) => kind.Name switch
    {
        "pdf" => Pdf(bytes, maxPages),
        "docx" => Word(bytes, maxPages),
        "text" or "markdown" => Text(DocumentKinds.TextOf(bytes), maxPages),
        _ when kind.Image => [new SourcePage(1, "", bytes, kind.ContentType)],
        _ => throw new DocumentUnreadableException($"A {kind.Name} file isn't one the package reads."),
    };

    // ---- Plain text and Markdown: a page per form feed.

    internal static IReadOnlyList<SourcePage> Text(string text, int maxPages)
    {
        string[] pages = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\f');
        if (pages.Length > maxPages)
        {
            throw new DocumentUnreadableException($"It has {pages.Length} pages; at most {maxPages} are read.");
        }

        return [.. pages.Select((p, i) => new SourcePage(i + 1, p.Trim('\n')))];
    }

    // ---- PDF: its text layer, page by page; a page with (almost) no text is scanned, its largest image read by a model.

    internal static IReadOnlyList<SourcePage> Pdf(byte[] bytes, int maxPages)
    {
        PdfDocument pdf;
        try
        {
            pdf = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true });
        }
        catch (Exception e) when (e.GetType().Name.Contains("Password", StringComparison.Ordinal) || e.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new DocumentUnreadableException("The PDF is protected by a password.", e);
        }
        catch (Exception e)
        {
            throw new DocumentUnreadableException("The PDF is damaged and can't be read.", e);
        }

        using (pdf)
        {
            if (pdf.NumberOfPages > maxPages)
            {
                throw new DocumentUnreadableException($"It has {pdf.NumberOfPages} pages; at most {maxPages} are read.");
            }

            List<SourcePage> pages = [];
            for (int n = 1; n <= pdf.NumberOfPages; n++)
            {
                PdfPage page;
                try
                {
                    page = pdf.GetPage(n);
                }
                catch (Exception e)
                {
                    throw new DocumentUnreadableException($"Page {n} of the PDF is damaged.", e);
                }

                string text = PdfText(page);
                int letters = text.Count(char.IsLetterOrDigit);
                if (letters < ScannedBelow && LargestImage(page) is { } image)
                {
                    pages.Add(new SourcePage(n, text, image.Bytes, image.Type));
                }
                else
                {
                    pages.Add(new SourcePage(n, text));
                }
            }

            return pages;
        }
    }

    private sealed record PdfLine(List<PdfWord> Words, double Top, double Bottom, double Size);

    /// <summary>A page's text in reading order: words in lines, lines in paragraphs, larger lines as headings.</summary>
    internal static string PdfText(PdfPage page)
    {
        List<PdfWord> words = [.. page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text))];
        if (words.Count == 0)
        {
            return "";
        }

        // Lines: words whose middles are within half a word's height of each other, top to bottom.
        List<PdfLine> lines = [];
        foreach (PdfWord word in words.OrderByDescending(w => w.BoundingBox.Bottom))
        {
            double middle = (word.BoundingBox.Top + word.BoundingBox.Bottom) / 2;
            PdfLine? line = lines.LastOrDefault(l => Math.Abs((l.Top + l.Bottom) / 2 - middle) <= Math.Max(1, (l.Top - l.Bottom) / 2));
            if (line is null)
            {
                lines.Add(new PdfLine([word], word.BoundingBox.Top, word.BoundingBox.Bottom, 0));
            }
            else
            {
                line.Words.Add(word);
            }
        }

        lines = [.. lines.Select(l => l with { Size = Median(l.Words.SelectMany(w => w.Letters).Select(x => x.PointSize)) })
            .OrderByDescending(l => l.Top)];
        double body = Median(lines.SelectMany(l => l.Words.SelectMany(w => w.Letters).Select(x => x.PointSize)));
        double gap = Median(lines.Zip(lines.Skip(1), (a, b) => a.Bottom - b.Top).Where(g => g >= 0).Select(g => g));

        StringBuilder sb = new();
        PdfLine? previous = null;
        foreach (PdfLine line in lines)
        {
            string text = LineText(line.Words);
            if (text.Length == 0)
            {
                continue;
            }

            bool heading = body > 0 && line.Size >= body * 1.2 && text.Length <= 150 && text.Any(char.IsLetter);
            if (previous is not null)
            {
                bool newParagraph = heading || previous.Size >= body * 1.2 || previous.Bottom - line.Top > Math.Max(gap * 1.8, 2);
                sb.Append(newParagraph ? "\n\n" : "\n");
            }

            if (heading)
            {
                sb.Append(line.Size >= body * 1.6 ? "# " : "## ");
            }

            sb.Append(text);
            previous = line;
        }

        return sb.ToString().Normalize(NormalizationForm.FormKC);
    }

    /// <summary>
    /// A line's words in reading order: left to right, or right to left when most of its letters are Arabic or Hebrew,
    /// each word's letters by where they are drawn, so text written in visual order reads as written.
    /// </summary>
    private static string LineText(List<PdfWord> words)
    {
        int rtl = words.Sum(w => w.Text.Count(IsRtl));
        int ltr = words.Sum(w => w.Text.Count(c => char.IsLetter(c) && !IsRtl(c)));
        bool rightToLeft = rtl > ltr;
        IEnumerable<PdfWord> ordered = rightToLeft ? words.OrderByDescending(w => w.BoundingBox.Right) : words.OrderBy(w => w.BoundingBox.Left);
        return string.Join(' ', ordered.Select(w => rightToLeft && w.Text.Any(IsRtl)
            ? string.Concat(w.Letters.OrderByDescending(l => l.BoundingBox.Left).Select(l => l.Value))
            : w.Text)).Trim();
    }

    private static bool IsRtl(char c) => c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿';

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = [.. values.Where(v => v > 0).Order()];
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }

    private sealed record PageImage(byte[] Bytes, string Type);

    /// <summary>A scanned page's image: its largest that is a PNG or JPEG as it's kept, or can be made a PNG.</summary>
    private static PageImage? LargestImage(PdfPage page)
    {
        foreach (IPdfImage image in page.GetImages().OrderByDescending(i => i.BoundingBox.Width * i.BoundingBox.Height))
        {
            try
            {
                if (image.TryGetPng(out byte[]? png) && png is { Length: > 0 })
                {
                    return new PageImage(png, "image/png");
                }

                byte[] raw = image.RawBytes.ToArray();
                if (raw.Length > 3 && raw[0] == 0xFF && raw[1] == 0xD8 && raw[2] == 0xFF)
                {
                    return new PageImage(raw, "image/jpeg");
                }
            }
            catch (Exception)
            {
                // An image PdfPig can't decode: try the next.
            }
        }

        return null;
    }

    // ---- Word: paragraphs with headings by style, tables as rows, a page at each page break.

    internal static IReadOnlyList<SourcePage> Word(byte[] bytes, int maxPages)
    {
        WordprocessingDocument doc;
        try
        {
            doc = WordprocessingDocument.Open(new MemoryStream(bytes, writable: false), false);
        }
        catch (Exception e)
        {
            throw new DocumentUnreadableException("The Word file is damaged or protected, and can't be read.", e);
        }

        using (doc)
        {
            Body body = doc.MainDocumentPart?.Document?.Body ?? throw new DocumentUnreadableException("The Word file has no text.");
            Dictionary<string, int> headingStyles = HeadingStyles(doc.MainDocumentPart.StyleDefinitionsPart?.Styles);
            WordPages pages = new(maxPages);
            foreach (OpenXmlElement element in body.ChildElements)
            {
                switch (element)
                {
                    case Paragraph p:
                        Paragraph(p, headingStyles, pages);
                        break;
                    case Table t:
                        foreach (TableRow row in t.Elements<TableRow>())
                        {
                            string cells = string.Join(" | ", row.Elements<TableCell>().Select(c => string.Join(' ', c.Descendants<Text>().Select(x => x.Text)).Trim()));
                            if (cells.Trim('|', ' ').Length > 0)
                            {
                                pages.Line(cells);
                            }
                        }

                        pages.Paragraph();
                        break;
                }
            }

            return pages.Done();
        }
    }

    private static void Paragraph(Paragraph p, Dictionary<string, int> headingStyles, WordPages pages)
    {
        ParagraphProperties? props = p.ParagraphProperties;
        if (props?.PageBreakBefore is { } before && (before.Val is null || before.Val.Value))
        {
            pages.Break();
        }

        int level = props?.OutlineLevel?.Val is { } outline ? outline.Value + 1
            : props?.ParagraphStyleId?.Val?.Value is { } style && headingStyles.TryGetValue(style, out int l) ? l
            : 0;
        StringBuilder text = new();
        foreach (OpenXmlElement node in p.Descendants())
        {
            switch (node)
            {
                case Text t:
                    text.Append(t.Text);
                    break;
                case TabChar:
                    text.Append('\t');
                    break;
                case Break b when b.Type?.Value == BreakValues.Page:
                case LastRenderedPageBreak:
                    pages.Text(text.ToString(), level);
                    text.Clear();
                    pages.Break();
                    break;
                case Break:
                    text.Append('\n');
                    break;
            }
        }

        pages.Text(text.ToString(), level);
        pages.Paragraph();
        if (props?.SectionProperties?.GetFirstChild<SectionType>()?.Val?.Value is { } type && type != SectionMarkValues.Continuous)
        {
            pages.Break();
        }
    }

    /// <summary>The styles that are headings, by id: their names (<c>heading 1</c>, <c>Title</c>) or outline level.</summary>
    private static Dictionary<string, int> HeadingStyles(Styles? styles)
    {
        Dictionary<string, int> result = new(StringComparer.Ordinal);
        foreach (Style style in styles?.Elements<Style>() ?? [])
        {
            if (style.StyleId?.Value is not { } id)
            {
                continue;
            }

            string name = style.StyleName?.Val?.Value?.ToLowerInvariant() ?? "";
            if (name == "title")
            {
                result[id] = 1;
            }
            else if (name.StartsWith("heading ", StringComparison.Ordinal) && int.TryParse(name[8..], out int n))
            {
                result[id] = Math.Clamp(n, 1, 6);
            }
            else if (style.StyleParagraphProperties?.OutlineLevel?.Val is { } outline && outline.Value < 9)
            {
                result[id] = outline.Value + 1;
            }
        }

        return result;
    }

    /// <summary>A Word file's pages as they're made: a new one at a break once the current one has text.</summary>
    private sealed class WordPages(int maxPages)
    {
        private readonly List<SourcePage> _pages = [];
        private readonly StringBuilder _page = new();
        private bool _paragraphOpen;

        public void Text(string text, int level)
        {
            text = text.Trim();
            if (text.Length == 0)
            {
                return;
            }

            if (!_paragraphOpen && _page.Length > 0)
            {
                _page.Append("\n\n");
            }

            if (level > 0 && !_paragraphOpen)
            {
                _page.Append(new string('#', Math.Min(level, 6))).Append(' ');
            }

            _page.Append(_paragraphOpen ? " " + text : text);
            _paragraphOpen = true;
        }

        public void Line(string text)
        {
            if (_page.Length > 0)
            {
                _page.Append('\n');
            }

            _page.Append(text);
            _paragraphOpen = false;
        }

        public void Paragraph() => _paragraphOpen = false;

        public void Break()
        {
            _paragraphOpen = false;
            if (_page.Length == 0)
            {
                return;
            }

            _pages.Add(new SourcePage(_pages.Count + 1, _page.ToString()));
            _page.Clear();
            if (_pages.Count > maxPages)
            {
                throw new DocumentUnreadableException($"It has more than {maxPages} pages; at most {maxPages} are read.");
            }
        }

        public IReadOnlyList<SourcePage> Done()
        {
            Break();
            return _pages.Count == 0 ? [new SourcePage(1, "")] : _pages;
        }
    }
}
