using System.Text;
using AbpDocuments.Reading;

namespace AbpDocuments.Tests;

/// <summary>Files known by their bytes, their pages read with headings, and pages cut into passages, without a database.</summary>
public class ReadingTests
{
    [Fact]
    public void A_file_is_known_by_its_bytes_not_its_name()
    {
        Assert.Equal(DocumentKinds.Pdf, DocumentKinds.Sniff(TestFiles.Pdf(("Terms", ["One line."])), "report.docx"));
        Assert.Equal(DocumentKinds.Word, DocumentKinds.Sniff(TestFiles.Word("T", "a", "B", "b"), "letter.pdf"));
        Assert.Equal(DocumentKinds.Png, DocumentKinds.Sniff(TestFiles.Png(), "photo.jpg"));
        Assert.Equal(DocumentKinds.Text, DocumentKinds.Sniff("Hello, \u0645\u0631\u062D\u0628\u0627\n"u8.ToArray(), "notes.txt"));
        Assert.Equal(DocumentKinds.Markdown, DocumentKinds.Sniff("# Title\n\nBody"u8.ToArray(), "readme.md"));

        // A zip that isn't a Word file, a program, and bytes that aren't UTF-8 are refused.
        Assert.Null(DocumentKinds.Sniff(TestFiles.Zip(), "file.docx"));
        Assert.Null(DocumentKinds.Sniff([0x4D, 0x5A, 0x90, 0x00, 0x03], "setup.txt"));
        Assert.Null(DocumentKinds.Sniff([0xC3, 0x28, 0x41], "bad.txt"));
        Assert.Null(DocumentKinds.Sniff([], "empty.txt"));
    }

    [Fact]
    public void A_PDFs_pages_keep_their_numbers_and_larger_lines_are_headings()
    {
        byte[] pdf = TestFiles.Pdf(
            ("Lease terms", ["The lease of the north warehouse ends on 30 June 2031.", "Rent is reviewed every two years."]),
            ("Payments", ["Rent is paid quarterly, in advance."]));

        IReadOnlyList<SourcePage> pages = PageReaders.Read(DocumentKinds.Pdf, pdf, 10);

        Assert.Equal([1, 2], pages.Select(p => p.Number));
        Assert.All(pages, p => Assert.False(p.NeedsModel));
        Assert.StartsWith("# Lease terms", pages[0].Text);
        Assert.Contains("ends on 30 June 2031.", pages[0].Text);
        Assert.StartsWith("# Payments", pages[1].Text);
        Assert.Throws<DocumentUnreadableException>(() => PageReaders.Read(DocumentKinds.Pdf, pdf, 1));
        Assert.Throws<DocumentUnreadableException>(() => PageReaders.Read(DocumentKinds.Pdf, [.. "%PDF-1.7 nothing else"u8], 10));
    }

    [Fact]
    public void A_scanned_PDF_page_is_its_image_for_a_model_to_read()
    {
        SourcePage page = Assert.Single(PageReaders.Read(DocumentKinds.Pdf, TestFiles.ScannedPdf(), 10));
        Assert.True(page.NeedsModel);
        Assert.Equal("image/png", page.ImageType);
        Assert.Equal(DocumentKinds.Png, DocumentKinds.Sniff(page.Image!, null));
    }

    [Fact]
    public void A_Word_files_headings_come_from_its_styles_tables_are_rows_and_a_page_break_starts_a_page()
    {
        byte[] docx = TestFiles.Word("\u0633\u064A\u0627\u0633\u0629 \u0627\u0644\u0625\u062C\u0627\u0632\u0627\u062A", "Staff take 30 days of leave a year.", "Carrying over",
            "Up to 5 days carry over to the next year.", [["Grade", "Days"], ["A", "30"]]);

        IReadOnlyList<SourcePage> pages = PageReaders.Read(DocumentKinds.Word, docx, 10);

        Assert.Equal(2, pages.Count);
        Assert.StartsWith("# \u0633\u064A\u0627\u0633\u0629", pages[0].Text);
        Assert.Contains("Grade | Days", pages[0].Text);
        Assert.Contains("A | 30", pages[0].Text);
        Assert.StartsWith("## Carrying over", pages[1].Text);
        Assert.Contains("carry over to the next year", pages[1].Text);
    }

    [Fact]
    public void A_Word_file_that_unzips_to_far_more_than_it_is_is_refused_before_it_is_unzipped()
    {
        byte[] docx = TestFiles.Word("Title", "Body", "Second", "More");
        using MemoryStream bomb = new();
        bomb.Write(docx);
        using (System.IO.Compression.ZipArchive zip = new(bomb, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
        {
            using Stream big = zip.CreateEntry("word/media/padding.bin", System.IO.Compression.CompressionLevel.SmallestSize).Open();
            big.Write(new byte[8 * 1024 * 1024]);
        }

        DocumentUnreadableException refused = Assert.Throws<DocumentUnreadableException>(() => PageReaders.Read(DocumentKinds.Word, bomb.ToArray(), 10));
        Assert.Contains("unzips to", refused.Message);
        Assert.Throws<DocumentUnreadableException>(() => PageReaders.Read(DocumentKinds.Word, docx, new ReadLimits(10, MaxUnzippedBytes: 100), default));

        // What's really read counts too, whatever the zip's headers say: a part's XML past half the limit in characters.
        byte[] lengthy = TestFiles.Word("Title", string.Join(' ', Enumerable.Repeat("words", 120_000)), "Second", "More");
        DocumentUnreadableException read = Assert.Throws<DocumentUnreadableException>(() =>
            PageReaders.Read(DocumentKinds.Word, lengthy, new ReadLimits(10, MaxUnzippedBytes: 1024 * 1024), default));
        Assert.Contains("unzips to more than 1 MB", read.Message);
    }

    [Fact]
    public void Text_past_the_limits_is_refused_with_the_reason()
    {
        byte[] onePage = Encoding.UTF8.GetBytes(new string('a', 600_000));
        Assert.Contains("Page 1 has 600,000 characters", Assert.Throws<DocumentUnreadableException>(() => PageReaders.Read(DocumentKinds.Text, onePage, 10)).Message);
        byte[] pages = Encoding.UTF8.GetBytes(string.Join('', Enumerable.Repeat(new string('b', 100), 5)));
        Assert.Contains("500 characters", Assert.Throws<DocumentUnreadableException>(() =>
            PageReaders.Read(DocumentKinds.Text, pages, new ReadLimits(10, MaxCharacters: 400), default)).Message);
        Assert.Throws<DocumentUnreadableException>(() => PageReaders.Read(DocumentKinds.Pdf,
            TestFiles.Pdf(("Terms", ["A line of text that is long enough."])), new ReadLimits(10, MaxCharacters: 10), default));
    }

    [Fact]
    public void Plain_text_is_a_page_per_form_feed()
    {
        IReadOnlyList<SourcePage> pages = PageReaders.Read(DocumentKinds.Text, "First page.\fSecond page."u8.ToArray(), 10);
        Assert.Equal(["First page.", "Second page."], pages.Select(p => p.Text));
    }

    [Fact]
    public void Passages_stay_within_a_page_keep_their_heading_and_overlap()
    {
        string longParagraph = string.Join(' ', Enumerable.Range(1, 60).Select(i => $"Clause {i} says the tenant keeps the yard clean."));
        IReadOnlyList<CutPassage> cut = Passages.Cut(
        [
            (1, "# Lease terms\n\nThe lease ends in 2031.\n\n" + longParagraph),
            (2, "Rent is paid quarterly.\n## Payments\nLate rent costs 2% a month."),
        ], length: 400, overlap: 60);

        Assert.True(cut.Count >= 5, $"{cut.Count} passages");
        Assert.All(cut.Where(p => p.Page == 1), p => Assert.Equal("Lease terms", p.Heading));
        Assert.All(cut, p => Assert.True(p.Text.Length <= 400 + 60, $"{p.Text.Length} characters"));
        Assert.Equal(Enumerable.Range(0, cut.Count), cut.Select(p => p.Ordinal));

        // The next passage starts with the end of the one before.
        CutPassage a = cut[1], b = cut[2];
        Assert.Equal(1, b.Page);
        Assert.Contains(a.Text[^20..].Trim().Split(' ')[^1], b.Text[..80]);

        // Page 2 carries the heading on until its own.
        CutPassage rent = Assert.Single(cut, p => p.Text.StartsWith("Rent is paid", StringComparison.Ordinal));
        Assert.Equal((2, "Lease terms"), (rent.Page, rent.Heading));
        CutPassage late = Assert.Single(cut, p => p.Text.Contains("Late rent", StringComparison.Ordinal));
        Assert.Equal((2, "Payments"), (late.Page, late.Heading));
        Assert.StartsWith("Payments\n\n", late.ForEmbedding);
    }

    [Fact]
    public void Arabic_is_made_one_form_for_search()
    {
        // Diacritics and tatweel go; hamza'd alefs, alef maqsura and taa marbuta are made one.
        Assert.Equal("\u0627\u0644\u0645\u062F\u064A\u0646\u0647", ArabicText.Normalize("\u0627\u0644\u0652\u0645\u064E\u062F\u0640\u0650\u064A\u0646\u064E\u0629"));
        Assert.Equal("\u0627\u0627\u0627", ArabicText.Normalize("\u0623\u0625\u0622"));
        Assert.Equal("\u0639\u064A\u0633\u064A", ArabicText.Normalize("\u0639\u064A\u0633\u0649"));
        // Presentation forms from a PDF become letters.
        Assert.Equal("\u0644\u0627", ArabicText.Normalize("\uFEFB"));
        Assert.Equal("Rent 2031", ArabicText.Normalize("Rent 2031"));
    }

    [Fact]
    public void A_files_name_keeps_no_folders_or_control_characters()
    {
        Assert.Equal("report.pdf", DocumentsService.NameOf("C:\\Users\\x\\report.pdf", DocumentKinds.Pdf));
        Assert.Equal("passwd.txt", DocumentsService.NameOf("../../etc/passwd", DocumentKinds.Text));
        Assert.Equal("a b.pdf", DocumentsService.NameOf("a\u0007 b.pdf", DocumentKinds.Pdf));
        Assert.Equal("document.docx", DocumentsService.NameOf("  ", DocumentKinds.Word));
        Assert.Equal(DocumentsLimits.NameLength, DocumentsService.NameOf(new string('x', 400) + ".pdf", DocumentKinds.Pdf).Length);
        Assert.True(Encoding.UTF8.GetByteCount(DocumentsService.NameOf("\u0639\u0642\u062F.pdf", null)) > 0);
    }
}
