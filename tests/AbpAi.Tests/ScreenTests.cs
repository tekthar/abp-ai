using Microsoft.Extensions.Options;

namespace AbpAi.Tests;

/// <summary>0.8.0's: what the shared settings screens found missing, in the parts that need no database.</summary>
public class ScreenTests
{
    private static readonly Guid Ben = Guid.NewGuid(), Agent = Guid.NewGuid();

    [Fact]
    public async Task A_host_that_names_no_one_still_builds_and_usage_shows_ids_alone()
    {
        IAiAccounts accounts = new NoAiGroups();
        Assert.Empty(await accounts.NamesOfAsync([Ben, Agent], default));

        AiCallRow row = AiUsageService.Row(Call(Agent, Ben), "FIN");
        Assert.Equal((Agent, Ben, (string?)null, (string?)null), (row.AccountId!.Value, row.OnBehalfOf!.Value, row.AccountName, row.OnBehalfOfName));
    }

    [Fact]
    public void A_call_names_its_account_and_the_person_it_was_for_as_the_host_does()
    {
        Dictionary<Guid, string> names = new() { [Agent] = "Budget agent", [Ben] = "Ben" };
        AiCallRow row = AiUsageService.Row(Call(Agent, Ben), "FIN", names);
        Assert.Equal(("Budget agent", "Ben", "FIN"), (row.AccountName, row.OnBehalfOfName, row.Scope));

        // Someone the host doesn't name is left unnamed; a call with no one has no names.
        Assert.Null(AiUsageService.Row(Call(Guid.NewGuid(), null), null, names).AccountName);
        AiCallRow nobody = AiUsageService.Row(Call(null, null), null, names);
        Assert.Equal(((string?)null, (string?)null), (nobody.AccountName, nobody.OnBehalfOfName));
    }

    [Fact]
    public void The_sites_settings_say_whether_the_host_keeps_the_connections()
    {
        AiSite site = new(AiSite.TheId);
        Assert.False(Admin(new StoredAiConnections(null!, null!)).Info(site).ConnectionsKeptByHost);
        Assert.True(Admin(new HostConnections()).Info(site).ConnectionsKeptByHost);
    }

    /// <summary>The admin service with what its database-free parts need; the rest is left out.</summary>
    private static AiAdminService Admin(IAiConnectionSource source) =>
        new(null!, new NoAiRights(), new AiScopeChains(new FlatAiScopes(), Options.Create(new AiOptions())), new AiDataRules(Options.Create(new AiOptions())), [],
            null!, source, Options.Create(new AiOptions()));

    private static AiUsageRecord Call(Guid? account, Guid? onBehalfOf) =>
        new(Guid.NewGuid(), DateTime.UtcNow, null, [], account, onBehalfOf, "ask", "smart", Guid.NewGuid(), "cloud", "demo", "cloud-large", "internal", 0m, false);

    /// <summary>Connections a host keeps: none, here.</summary>
    private sealed class HostConnections : IAiConnectionSource
    {
        public Task<IReadOnlyList<AiConnectionDescriptor>> ListAsync(IReadOnlyCollection<Guid> scopeIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AiConnectionDescriptor>>([]);

        public Task<string?> KeyAsync(Guid connectionId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
