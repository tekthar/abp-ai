namespace AbpAi.Host.Tests;

/// <summary>
/// 0.8.0's: what the shared settings screens found missing. People named in usage as the host names them.
/// </summary>
public sealed partial class GatewayHostTests
{
    [Fact]
    public async Task Usage_by_person_and_by_account_and_recent_calls_name_people_as_the_host_does_asking_once_a_query()
    {
        Guid stranger = Guid.NewGuid(); // someone the host doesn't name
        await Ask(Budget, Ben);
        await Ask(Budget, Agent, onBehalfOf: Cy);
        await Ask(Pipeline, stranger);

        AiUsageService usage = Get<AiUsageService>();
        DateTime day = DateTime.UtcNow.Date;
        int asked = _namesAsked;
        AiUsageReport byPerson = await usage.QueryAsync(SiteAdmin, new AiUsageQuery(day, day.AddDays(1), null, AiUsageGroups.Person), default);
        Assert.Equal(1, _namesAsked - asked);
        Assert.Equal(new (string, string?)[] { (Ben.ToString(), "Ben"), (Cy.ToString(), "Cy"), (stranger.ToString(), null) },
            byPerson.Rows.Select(r => (r.Key, r.Title)).OrderBy(r => r.Title ?? "~", StringComparer.Ordinal));

        AiUsageReport byAccount = await usage.QueryAsync(SiteAdmin, new AiUsageQuery(day, day.AddDays(1), null, AiUsageGroups.Account), default);
        Assert.Equal(new string?[] { "Ben", "Budget agent", null }, byAccount.Rows.Select(r => r.Title).OrderBy(t => t ?? "~", StringComparer.Ordinal));

        // The calls themselves name their account and the person each was for, with one ask for them all.
        asked = _namesAsked;
        IReadOnlyList<AiCallRow> calls = await usage.RecentAsync(SiteAdmin, new AiUsageQuery(day, day.AddDays(1)), 10, default);
        Assert.Equal(1, _namesAsked - asked);
        AiCallRow agents = calls.Single(c => c.AccountId == Agent);
        Assert.Equal(("Budget agent", "Cy"), (agents.AccountName, agents.OnBehalfOfName));
        AiCallRow bens = calls.Single(c => c.AccountId == Ben);
        Assert.Equal(("Ben", (string?)null), (bens.AccountName, bens.OnBehalfOfName));
        Assert.Null(calls.Single(c => c.AccountId == stranger).AccountName);

        // A person's own, by person, is named too; usage by purpose asks for no names.
        Assert.Equal("Cy", Assert.Single((await usage.MineAsync(new AiActor(Cy), day, day.AddDays(1), AiUsageGroups.Person, default)).Rows).Title);
        asked = _namesAsked;
        await usage.QueryAsync(SiteAdmin, new AiUsageQuery(day, day.AddDays(1), null, AiUsageGroups.Purpose), default);
        Assert.Equal(asked, _namesAsked);
    }

    /// <summary>The host's accounts, telling each time names are asked.</summary>
    private sealed class CountingAccounts(IAiAccounts inner, Action namesAsked) : IAiAccounts
    {
        public Task<IReadOnlyList<Guid>> GroupsOfAsync(Guid accountId, CancellationToken cancellationToken) => inner.GroupsOfAsync(accountId, cancellationToken);

        public Task<IReadOnlyDictionary<Guid, string>> NamesOfAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
        {
            namesAsked();
            return inner.NamesOfAsync(accountIds, cancellationToken);
        }
    }
}
