using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace AbpAi.Host.Tests;

/// <summary>
/// The admin API's document, kept in docs/openapi/abpai.json: what a host's screens generate their types from
/// (@abpjs/ui's ai area). The sample serves it at /openapi/abpai.json; this fails when the kept copy differs, and
/// ABP_AI_UPDATE_OPENAPI=1 writes it instead. Needs Postgres, as the other host tests.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class OpenApiContractTests : IAsyncLifetime
{
    private readonly string _database = "abp_ai_host_" + Guid.NewGuid().ToString("N")[..12];
    private WebApplicationFactory<Program>? _factory;

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"{GatewayHostTests.Server};Database={_database}");
        _factory = new WebApplicationFactory<Program>();
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        NpgsqlConnection.ClearAllPools();
        await using NpgsqlConnection connection = new($"{GatewayHostTests.Server};Database=postgres");
        await connection.OpenAsync();
        await using NpgsqlCommand drop = new($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task The_kept_document_is_the_one_the_admin_API_serves()
    {
        HttpClient client = _factory!.CreateClient();
        string served = await client.GetStringAsync("/openapi/abpai.json");
        JsonNode document = JsonNode.Parse(served)!;

        // Only AbpAi.HttpApi's actions: the sample's own call (api/ai/ask) is in no group of AbpAi's.
        JsonObject paths = document["paths"]!.AsObject();
        Assert.Contains("/api/ai/connections", paths.Select(p => p.Key));
        Assert.DoesNotContain("/api/ai/ask", paths.Select(p => p.Key));
        Assert.Equal(AbpAiModule.Version, (string?)document["info"]!["version"]);

        string text = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
        string kept = Path.Combine(RepositoryRoot(), "docs", "openapi", "abpai.json");
        if (Environment.GetEnvironmentVariable("ABP_AI_UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
            await File.WriteAllTextAsync(kept, text);
            return;
        }

        Assert.True(File.Exists(kept), "docs/openapi/abpai.json is missing: run the host tests with ABP_AI_UPDATE_OPENAPI=1.");
        string expected = (await File.ReadAllTextAsync(kept)).Replace("\r\n", "\n");
        Assert.True(expected == text,
            "docs/openapi/abpai.json differs from the admin API: run the host tests with ABP_AI_UPDATE_OPENAPI=1 and commit the change, "
            + "then regenerate the screens' types from it.");
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AbpAi.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("AbpAi.slnx not found above the test's folder.");
    }
}
