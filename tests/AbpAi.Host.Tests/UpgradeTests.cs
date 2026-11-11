using AbpAi.SampleHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AbpAi.Host.Tests;

/// <summary>
/// A site on 0.2.0's tables moving to this version: its database made by the sample's AbpAi020 migration alone, with a
/// connection, an alias, a limit, a call and the site's settings in it as 0.2.0 kept them; then the sample starts and runs
/// the rest. Needs Postgres, as <see cref="GatewayHostTests"/> does.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class UpgradeTests : IAsyncLifetime
{
    private static readonly AiActor SiteAdmin = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly Guid Ben = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    private readonly string _database = "abp_ai_host_" + Guid.NewGuid().ToString("N")[..12];
    private WebApplicationFactory<Program>? _factory;

    public Task InitializeAsync() => Task.CompletedTask;

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
    public async Task A_site_on_the_last_versions_tables_migrates_and_keeps_its_connections_aliases_limits_and_usage()
    {
        string database = $"{GatewayHostTests.Server};Database={_database}";
        await using (SampleDbContext db = new(new DbContextOptionsBuilder<SampleDbContext>().UseNpgsql(database).Options))
        {
            await db.GetService<IMigrator>().MigrateAsync("AbpAi020");
        }

        Guid connection = Guid.NewGuid(), limit = Guid.NewGuid();
        await using (NpgsqlConnection sql = new(database))
        {
            await sql.OpenAsync();
            await using NpgsqlCommand rows = new($$"""
                INSERT INTO ai.connections ("Id", "ScopeId", "Name", "Provider", "Endpoint", "ProtectedKey", "Models", "DataClasses", "Labels", "Settings", "InputPrice",
                    "OutputPrice", "ModelPrices", "StructuredOutput", "Enabled", "CreatedAt")
                VALUES ('{{connection}}', NULL, 'old-demo', 'demo', '', NULL, '["old-model"]', '["public","internal","aggregates","rows","sensitive"]', '[]', '{}', 0,
                    1000, '{}', false, true, now());
                INSERT INTO ai.aliases ("Id", "ScopeId", "Name", "Targets", "UpdatedAt")
                VALUES ('{{Guid.NewGuid()}}', NULL, 'smart', '[{"connectionId":"{{connection}}","model":null}]', now());
                INSERT INTO ai.limits ("Id", "ScopeId", "Kind", "AccountId", "GroupId", "Period", "Amount", "CreatedAt")
                VALUES ('{{limit}}', NULL, 'total', NULL, NULL, 'day', 0.03, now());
                INSERT INTO ai.usage ("Id", "At", "State", "ScopeId", "ScopePath", "AccountId", "OnBehalfOf", "Purpose", "AboutKind", "AboutId", "Alias", "ConnectionId",
                    "Connection", "Provider", "Model", "DataClass", "Streamed", "Reserved", "Cost", "InputTokens", "OutputTokens", "DurationMs", "Error")
                VALUES ('{{Guid.NewGuid()}}', now(), 'done', NULL, '{}', '{{Ben}}', NULL, 'ask', NULL, NULL, 'smart', '{{connection}}', 'old-demo', 'demo', 'old-model',
                    'internal', false, 0.02, 0.02, 10, 20, 5, NULL);
                INSERT INTO ai.site ("Id", "ScopesMayConnect", "KeepPrompts", "PromptDays", "ScopeDataClasses", "UpdatedAt")
                VALUES ('{{AiSite.TheId}}', false, false, 30, '["public","internal"]', now());
                """, sql);
            await rows.ExecuteNonQueryAsync();
        }

        Environment.SetEnvironmentVariable("ConnectionStrings__Default", database);
        _factory = new WebApplicationFactory<Program>();
        _ = _factory.Server; // runs the rest of the migrations
        using IServiceScope scope = _factory.Services.CreateScope();
        AiAdminService admin = scope.ServiceProvider.GetRequiredService<AiAdminService>();
        IAi ai = scope.ServiceProvider.GetRequiredService<IAi>();

        // What 0.3.0 adds starts as 0.2.0 behaved: every scope open, the limit counting cost, the model taking everything.
        Assert.Equal(AiScopeAccessModes.Open, (await admin.GetSiteAsync(SiteAdmin, default)).ScopeAccess);
        AiLimitInfo kept = Assert.Single(await admin.ListLimitsAsync(SiteAdmin, null, default));
        Assert.Equal((limit, AiLimitMeasures.Cost, AiPeriods.Day), (kept.Id, kept.Measure, kept.Period));
        Assert.Empty(Assert.Single(await admin.ListConnectionsAsync(SiteAdmin, null, default)).ModelSettings);
        AiChoice choice = await ai.ResolveAsync("smart", null, AiDataClasses.Internal);
        Assert.Equal(("old-model", "old-model", true, true), (choice.Model, choice.ModelName, choice.Tools, choice.Images));

        // The day's call before counts: 0.02 spent, and this one's 0.02 doesn't fit 0.03.
        AiRequest ask = new("smart", AiMessages.User("Totals?")) { AccountId = Ben, DataClass = AiDataClasses.Internal, MaxTokens = 20 };
        AiRefusedException refused = await Assert.ThrowsAsync<AiRefusedException>(() => ai.CompleteAsync(ask));
        Assert.Equal((AiRefusalCodes.OverLimit, limit, AiLimitMeasures.Cost, 0.02m), (refused.Code, refused.Limit!.LimitId, refused.Limit.Measure, refused.Limit.Spent));
        await admin.SetLimitAsync(SiteAdmin, null, new AiLimitInput(AiLimitKinds.Total, AiPeriods.Day, 1), default);
        Assert.Equal("old-demo", (await ai.CompleteAsync(ask)).Connection);
    }
}
