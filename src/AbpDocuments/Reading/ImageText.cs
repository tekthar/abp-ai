using System.Text.Json;
using System.Text.Json.Nodes;
using AbpAi;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace AbpDocuments.Reading;

/// <summary>
/// Reads a scanned page or a picture through AbpAi: the image, and a request for its text as written. The call carries
/// the document's data class, scope and adder, so only a model allowed that class reads it, and AbpAi counts it.
/// </summary>
public class ImageText(IAi ai, IOptions<DocumentsOptions> options) : ITransientDependency
{
    /// <summary>The most bytes an image may have to be sent (a provider's limit, base64 included): 3.75 MB.</summary>
    public const int MaxImageBytes = 3_750_000;

    internal const string Prompt = """
        Write out all the text in this image exactly as written, in its own language and reading order. Mark headings
        with Markdown's # (## for smaller ones). Write a table as rows with | between cells. Describe a chart, a stamp
        or a signature in one line in square brackets. Add nothing else: no comments, no translation. If there's no
        text, answer [no text].
        """;

    public async Task<string> ReadAsync(Document document, byte[] image, string mediaType, int page, CancellationToken cancellationToken)
    {
        if (image.Length > MaxImageBytes)
        {
            throw new DocumentUnreadableException($"Page {page}'s image is {image.Length / 1_000_000.0:0.#} MB; a model reads images of at most 3.7 MB.");
        }

        JsonArray content =
        [
            new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = Convert.ToBase64String(image) },
            },
            new JsonObject { ["type"] = "text", ["text"] = Prompt },
        ];
        JsonElement messages = JsonSerializer.SerializeToElement(new JsonArray { new JsonObject { ["role"] = "user", ["content"] = content } });
        AiReply reply = await ai.CompleteAsync(new AiRequest(options.Value.ReadAlias, messages)
        {
            MaxTokens = 4096,
            Purpose = DocumentsPurposes.Read,
            ScopeId = document.ScopeId,
            AccountId = document.AddedBy,
            DataClass = document.DataClass,
            About = new AiSubject("document", document.Id.ToString()),
        }, cancellationToken);
        string text = reply.Text.Trim();
        return text == "[no text]" ? "" : text;
    }
}
