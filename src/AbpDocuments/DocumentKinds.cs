using System.IO.Compression;
using System.Text;
using AbpCluster.Storage;

namespace AbpDocuments;

/// <summary>A kind of document the package reads: its name, its media type, and whether a model reads it from an image.</summary>
public sealed record DocumentKind(string Name, string ContentType, string Extension, bool Image = false);

/// <summary>
/// What a file is, by its bytes rather than its name or the type a browser sent: a PDF, a Word file (a zip holding
/// <c>word/document.xml</c>), UTF-8 text (Markdown when its name says <c>.md</c>), or a PNG, JPEG or WebP picture.
/// </summary>
public static class DocumentKinds
{
    public static readonly DocumentKind Pdf = new("pdf", "application/pdf", "pdf");
    public static readonly DocumentKind Word = new("docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "docx");
    public static readonly DocumentKind Text = new("text", "text/plain; charset=utf-8", "txt");
    public static readonly DocumentKind Markdown = new("markdown", "text/markdown; charset=utf-8", "md");
    public static readonly DocumentKind Png = new("png", "image/png", "png", Image: true);
    public static readonly DocumentKind Jpeg = new("jpeg", "image/jpeg", "jpg", Image: true);
    public static readonly DocumentKind WebP = new("webp", "image/webp", "webp", Image: true);

    public static IReadOnlyList<DocumentKind> All { get; } = [Pdf, Word, Text, Markdown, Png, Jpeg, WebP];

    /// <summary>The extensions a file picker may offer (the bytes decide all the same).</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".pdf", ".docx", ".txt", ".md", ".markdown", ".png", ".jpg", ".jpeg", ".webp"];

    public static DocumentKind? Named(string name) => All.FirstOrDefault(k => k.Name == name);

    /// <summary>The kind the bytes are, or null for one the package doesn't read. The name only tells Markdown from text.</summary>
    public static DocumentKind? Sniff(byte[] bytes, string? fileName)
    {
        FileKind? kind = FileKinds.Sniff(bytes);
        if (kind == FileKinds.Pdf)
        {
            return Pdf;
        }

        if (kind == FileKinds.Png)
        {
            return Png;
        }

        if (kind == FileKinds.Jpeg)
        {
            return Jpeg;
        }

        if (kind == FileKinds.WebP)
        {
            return WebP;
        }

        if (kind == FileKinds.Zip)
        {
            return IsWord(bytes) ? Word : null;
        }

        if (kind is null && IsText(bytes))
        {
            string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            return ext is ".md" or ".markdown" ? Markdown : Text;
        }

        return null;
    }

    private static bool IsWord(byte[] bytes)
    {
        try
        {
            using ZipArchive zip = new(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            return zip.GetEntry("word/document.xml") is not null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>UTF-8 (with or without its mark) with no NUL and few control characters: text, not a binary file.</summary>
    internal static bool IsText(byte[] bytes)
    {
        if (bytes.Length == 0 || Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return false;
        }

        string text;
        try
        {
            text = Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        int controls = text.Count(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t' or '\f'));
        return controls <= text.Length / 100;
    }

    /// <summary>Text's bytes as a string, without its mark.</summary>
    internal static string TextOf(byte[] bytes) => Strict.GetString(bytes).TrimStart('﻿');
}
