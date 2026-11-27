using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace AbpDocuments.Tests;

/// <summary>Files made in the tests: PDFs with a text layer or only an image, Word files with headings, breaks and tables, a picture.</summary>
internal static class TestFiles
{
    /// <summary>A PDF whose pages each have a heading (large) and lines of body text.</summary>
    public static byte[] Pdf(params (string Heading, string[] Lines)[] pages)
    {
        PdfDocumentBuilder builder = new();
        PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach ((string heading, string[] lines) in pages)
        {
            PdfPageBuilder page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            page.AddText(heading, 22, new PdfPoint(40, 780), font);
            double y = 740;
            foreach (string line in lines)
            {
                page.AddText(line, 11, new PdfPoint(40, y), font);
                y -= 15;
            }
        }

        return builder.Build();
    }

    /// <summary>A PDF of one page that's only a picture: a scanned page.</summary>
    public static byte[] ScannedPdf()
    {
        PdfDocumentBuilder builder = new();
        PdfPageBuilder page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        page.AddPng(Png(), new PdfRectangle(40, 40, 540, 780));
        return builder.Build();
    }

    /// <summary>A Word file: a title, paragraphs, a table, and a page break before its second part.</summary>
    public static byte[] Word(string title, string first, string afterBreakHeading, string afterBreak, string[][]? table = null)
    {
        using MemoryStream stream = new();
        using (WordprocessingDocument doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            MainDocumentPart main = doc.AddMainDocumentPart();
            StyleDefinitionsPart styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new Styles(
                new Style(new StyleName { Val = "heading 1" }) { StyleId = "Heading1", Type = StyleValues.Paragraph },
                new Style(new StyleName { Val = "heading 2" }) { StyleId = "Heading2", Type = StyleValues.Paragraph });
            Body body = new(
                new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }), new Run(new Text(title))),
                new Paragraph(new Run(new Text(first))));
            if (table is not null)
            {
                body.Append(new Table(table.Select(row => new TableRow(row.Select(cell => new TableCell(new Paragraph(new Run(new Text(cell)))))))));
            }

            body.Append(
                new Paragraph(new Run(new Break { Type = BreakValues.Page })),
                new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Heading2" }), new Run(new Text(afterBreakHeading))),
                new Paragraph(new Run(new Text(afterBreak))));
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(body);
        }

        return stream.ToArray();
    }

    /// <summary>A zip that isn't a Word file.</summary>
    public static byte[] Zip()
    {
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using StreamWriter w = new(zip.CreateEntry("readme.txt").Open());
            w.Write("hello");
        }

        return stream.ToArray();
    }

    /// <summary>An 8×8 grey PNG, made here (IHDR, a zlib IDAT, IEND, each with its CRC).</summary>
    public static byte[] Png()
    {
        const int size = 8;
        using MemoryStream raw = new();
        for (int y = 0; y < size; y++)
        {
            raw.WriteByte(0); // no filter
            for (int x = 0; x < size * 3; x++)
            {
                raw.WriteByte(128);
            }
        }

        using MemoryStream packed = new();
        using (ZLibStream z = new(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(z);
        }

        using MemoryStream png = new();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", [0, 0, 0, size, 0, 0, 0, size, 8, 2, 0, 0, 0]);
        Chunk(png, "IDAT", packed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        png.Write([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
        png.Write(typeBytes);
        png.Write(data);
        uint crc = Crc([.. typeBytes, .. data]);
        png.Write([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
    }

    private static uint Crc(byte[] bytes)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in bytes)
        {
            c ^= b;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
        }

        return c ^ 0xFFFFFFFF;
    }
}
