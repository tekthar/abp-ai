using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AbpAi.Providers;
using Microsoft.Extensions.Options;

namespace AbpAi.Tests;

/// <summary>
/// Embeddings (0.5.0): an OpenAI-compatible server's request and answer, and the demo model's vectors, which are the
/// same for the same text everywhere and near for texts that share words, so a search by meaning runs in tests.
/// </summary>
public class EmbeddingTests
{
    [Fact]
    public async Task An_OpenAI_compatible_server_is_asked_for_the_texts_vectors_and_they_come_back_in_order()
    {
        string? sent = null, url = null;
        Http http = new(request =>
        {
            url = request.RequestUri!.ToString();
            sent = request.Content!.ReadAsStringAsync().Result;
            // Out of order on purpose: each vector says which text it's for.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"index":1,"embedding":[0.5,0.25]},{"index":0,"embedding":[1,0]}],"model":"m","usage":{"prompt_tokens":7,"total_tokens":7}}"""),
            };
        });
        OpenAiCompatibleProvider provider = new(http, Options.Create(new AiOptions()));
        AiTarget target = new(Guid.NewGuid(), "vectors", "http://localhost:11434/v1", null, "nomic-embed-text", new Dictionary<string, string>());

        AiProviderEmbedding answer = await provider.EmbedAsync(new AiEmbedCall(target, ["first", "second"]) { Dimensions = 2, InputType = AiEmbedInputTypes.Query },
            CancellationToken.None);

        Assert.Equal("http://localhost:11434/v1/embeddings", url);
        JsonObject body = JsonNode.Parse(sent!)!.AsObject();
        Assert.Equal(("nomic-embed-text", 2, 2), ((string)body["model"]!, (int)body["dimensions"]!, body["input"]!.AsArray().Count));
        Assert.False(body.ContainsKey("input_type"), "only for a connection that says it takes one");
        Assert.Equal([1f, 0f], answer.Vectors[0]);
        Assert.Equal([0.5f, 0.25f], answer.Vectors[1]);
        Assert.Equal(7, answer.InputTokens);
        Assert.True(provider.CanEmbed);
        Assert.False(((IAiProvider)new AnthropicProvider(http)).CanEmbed);

        // Voyage AI's connection says it takes the texts' type.
        await provider.EmbedAsync(new AiEmbedCall(target with { Settings = new Dictionary<string, string> { ["input-type"] = "send" } }, ["q"]) { InputType = AiEmbedInputTypes.Query },
            CancellationToken.None);
        Assert.Equal("query", (string?)JsonNode.Parse(sent!)!["input_type"]);
    }

    [Fact]
    public void The_demo_models_vectors_are_the_same_everywhere_and_near_when_texts_share_words()
    {
        float[] invoice = DemoProvider.Vector("The invoice for March is overdue", 256);
        Assert.Equal(invoice, DemoProvider.Vector("the INVOICE for march, is overdue!", 256));
        Assert.Equal(1.0, Math.Sqrt(invoice.Sum(v => (double)v * v)), 5);

        double near = Cosine(invoice, DemoProvider.Vector("Is the March invoice overdue?", 256));
        double far = Cosine(invoice, DemoProvider.Vector("Rotate the backup keys on Sunday", 256));
        Assert.True(near > 0.7 && far < near, $"near {near:0.00}, far {far:0.00}");

        // Words in any script count; a text with none is zeros, not NaN.
        Assert.True(Cosine(DemoProvider.Vector("فاتورة شهر مارس", 64), DemoProvider.Vector("فاتورة مارس متأخرة", 64)) > 0.5);
        Assert.All(DemoProvider.Vector("— !", 16), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Texts_that_cant_be_embedded_as_asked_are_refused_before_anything_leaves()
    {
        Assert.NotNull(AiService.InvalidEmbed(new AiEmbedRequest("", ["a"])));
        Assert.NotNull(AiService.InvalidEmbed(new AiEmbedRequest("embed", [])));
        Assert.NotNull(AiService.InvalidEmbed(new AiEmbedRequest("embed", ["a", " "])));
        Assert.NotNull(AiService.InvalidEmbed(new AiEmbedRequest("embed", ["a"]) { Dimensions = 0 }));
        Assert.NotNull(AiService.InvalidEmbed(new AiEmbedRequest("embed", [.. Enumerable.Repeat("a", AiService.MaxEmbedInputs + 1)])));
        Assert.Null(AiService.InvalidEmbed(new AiEmbedRequest("embed", ["a", "b"])));
    }

    private static double Cosine(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum();

    private sealed class Http(Func<HttpRequestMessage, HttpResponseMessage> answer) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(answer));

        private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request));
        }
    }
}
