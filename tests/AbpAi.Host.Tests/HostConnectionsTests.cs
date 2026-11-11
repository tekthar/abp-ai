using System.Text.Json;
using AbpAi.SampleHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace AbpAi.Host.Tests;

/// <summary>
/// The sample with connections the host keeps (its own <see cref="IAiConnectionSource"/>), as a product with a
/// connections page of its own has them: the gateway's aliases, limits, usage and data rules on the host's connections,
/// and a provider of the host's own kind (<c>acme:bedrock</c>). Needs Postgres, as <see cref="GatewayHostTests"/> does.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class HostConnectionsTests : IAsyncLifetime
{
    private static readonly AiActor SiteAdmin = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly AiActor FinanceAdmin = new(Guid.Parse("00000000-0000-0000-0000-0000000000f1"));
    private static readonly Guid Ben = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Finance = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Budget = Guid.Parse("10000000-0000-0000-0000-000000000011");
    private static readonly Guid Sales = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Pipeline = Guid.Parse("10000000-0000-0000-0000-000000000021");

    private readonly string _database = "abp_ai_host_" + Guid.NewGuid().ToString("N")[..12];
    private readonly HostSource _source = new();
    private readonly AcmeProvider _acme = new();
    private WebApplicationFactory<Program>? _factory;

    // The host's connections: one of the site's on its own provider, one of Finance's own.
    private readonly AiConnectionDescriptor _bedrock = new(Guid.NewGuid(), null, "bedrock", AcmeProvider.TheKind)
    {
        Models = ["acme-large"], DataClasses = [.. AiDataClasses.Default], Labels = ["provider-cloud"], OutputPrice = 1000, HasKey = true,
    };

    private readonly AiConnectionDescriptor _financeOwn = new(Guid.NewGuid(), Finance, "finance-demo", "demo")
    {
        Models = ["fin-model"], DataClasses = [AiDataClasses.Public, AiDataClasses.Internal],
    };

    public async Task InitializeAsync()
    {
        _source.Connections.AddRange([_bedrock, _financeOwn]);
        _source.Keys[_bedrock.Id] = "host-key-1";
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"{GatewayHostTests.Server};Database={_database}");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Singleton<IAiConnectionSource>(_source));
            s.AddSingleton<IAiProvider>(_acme);
        }));
        _ = _factory.Server;
        await Get<AiAdminService>().SetAliasAsync(SiteAdmin, null, "smart", [new AiAliasTarget(_bedrock.Id)], default);
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
    public async Task A_call_goes_through_the_hosts_connection_and_its_key_is_asked_only_as_the_call_goes_out()
    {
        AiChoice choice = await Get<IAi>().ResolveAsync("smart", Budget, AiDataClasses.Rows);
        Assert.Equal((_bedrock.Id, "bedrock", AcmeProvider.TheKind, "acme-large"), (choice.ConnectionId, choice.Connection, choice.Provider, choice.Model));
        Assert.Empty(_source.KeysAsked);

        AiReply reply = await Ask(Budget);
        Assert.Equal(("From the host's provider.", "bedrock"), (reply.Text, reply.Connection));
        Assert.Equal("host-key-1", Assert.Single(_acme.Calls).Target.ApiKey);
        Assert.Equal([_bedrock.Id], _source.KeysAsked);
        AiUsageRecord record = await Db(db => db.AiUsage.SingleAsync(r => r.Id == reply.UsageId));
        Assert.Equal((_bedrock.Id, AcmeProvider.TheKind, AiUsageStates.Done, 0.005m), (record.ConnectionId, record.Provider, record.State, record.Cost));

        // A call a limit refuses never asks for the key.
        await Get<AiAdminService>().SetLimitAsync(SiteAdmin, Sales, new AiLimitInput(AiLimitKinds.Total, AiPeriods.Day, 0m), default);
        AiRefusedException refused = await Assert.ThrowsAsync<AiRefusedException>(() => Ask(Pipeline));
        Assert.Equal(AiRefusalCodes.OverLimit, refused.Code);
        Assert.Single(_source.KeysAsked);

        // The site's data rules hold on the host's connections as on AbpAi's own.
        _source.Connections[0] = _bedrock with { DataClasses = [AiDataClasses.Public] };
        Assert.Equal(AiRefusalCodes.DataNotAllowed, (await Assert.ThrowsAsync<AiRefusedException>(() => Ask(Budget))).Code);
    }

    [Fact]
    public async Task The_admin_lists_the_hosts_connections_and_leaves_setting_them_to_the_host()
    {
        AiAdminService admin = Get<AiAdminService>();
        Assert.False(admin.KeepsConnections);
        AiConnectionInfo listed = Assert.Single(await admin.ListConnectionsAsync(SiteAdmin, null, default));
        Assert.Equal((_bedrock.Id, AcmeProvider.TheKind, true), (listed.Id, listed.Provider, listed.HasKey));
        Assert.DoesNotContain("host-key-1", JsonSerializer.Serialize(listed));
        Assert.Equal("finance-demo", Assert.Single(await admin.ListConnectionsAsync(FinanceAdmin, Finance, default)).Name);

        AiConnectionInput input = new("mine", "demo", ["m"], [AiDataClasses.Public]);
        foreach (Func<Task> setting in new Func<Task>[]
                 {
                     () => admin.CreateConnectionAsync(SiteAdmin, null, input, default),
                     () => admin.UpdateConnectionAsync(SiteAdmin, _bedrock.Id, input, default),
                     () => admin.DeleteConnectionAsync(SiteAdmin, _bedrock.Id, default),
                 })
        {
            Assert.Equal(AiErrorCodes.HostConnections, (await Assert.ThrowsAsync<Volo.Abp.UserFriendlyException>(setting)).Code);
        }

        // A scope's own connection serves its scopes only while the site lets scopes connect, as AbpAi's own do.
        Assert.Equal(AiRefusalCodes.NoModel, (await Assert.ThrowsAsync<AiRefusedException>(() => Get<IAi>().ResolveAsync("fin-model", Budget, AiDataClasses.Internal))).Code);
        await admin.SetSiteAsync(SiteAdmin, new AiSiteSettings(true, false, 30, [AiDataClasses.Public, AiDataClasses.Internal], []), default);
        Assert.Equal("finance-demo", (await Get<IAi>().ResolveAsync("fin-model", Budget, AiDataClasses.Internal)).Connection);
        await admin.SetAliasAsync(FinanceAdmin, Finance, "own", [new AiAliasTarget(_financeOwn.Id)], default);
        await Assert.ThrowsAsync<Volo.Abp.UserFriendlyException>(() => admin.SetAliasAsync(SiteAdmin, Sales, "own", [new AiAliasTarget(_financeOwn.Id)], default));
    }

    private T Get<T>() where T : notnull => _factory!.Services.GetRequiredService<T>();

    private Task<AiReply> Ask(Guid scope) => Get<IAi>().CompleteAsync(new AiRequest("smart", AiMessages.User("Totals by region, please."))
    {
        ScopeId = scope, AccountId = Ben, DataClass = AiDataClasses.Rows, MaxTokens = 20, Purpose = "ask",
    });

    private async Task<T> Db<T>(Func<IAiDbContext, Task<T>> read)
    {
        using IServiceScope scope = _factory!.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AiDb>().RunAsync(read);
    }

    /// <summary>The host's connections, kept in memory, and every key asked of it.</summary>
    private sealed class HostSource : IAiConnectionSource
    {
        public List<AiConnectionDescriptor> Connections { get; } = [];

        public Dictionary<Guid, string> Keys { get; } = [];

        public List<Guid> KeysAsked { get; } = [];

        public Task<IReadOnlyList<AiConnectionDescriptor>> ListAsync(IReadOnlyCollection<Guid> scopeIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiConnectionDescriptor>>([.. Connections.Where(c => c.ScopeId is null || scopeIds.Contains(c.ScopeId.Value))]);

        public Task<string?> KeyAsync(Guid connectionId, CancellationToken cancellationToken)
        {
            lock (KeysAsked)
            {
                KeysAsked.Add(connectionId);
            }

            return Task.FromResult(Keys.GetValueOrDefault(connectionId));
        }
    }
}
