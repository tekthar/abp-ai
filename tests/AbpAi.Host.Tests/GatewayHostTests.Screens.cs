namespace AbpAi.Host.Tests;

/// <summary>
/// 0.8.0's: what the shared settings screens found missing. People named in usage as the host names them, and a scope's
/// own view for its admins.
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

    [Fact]
    public async Task A_scopes_admin_sees_the_models_open_to_it_and_the_aliases_and_limits_it_gets_from_above()
    {
        AiAdminService admin = Get<AiAdminService>();
        // The site lists access: Finance's opens on-prem's model and lets it connect, and Finance connects its own.
        await admin.SetSiteAsync(SiteAdmin, new AiSiteSettings(true, false, 30, [AiDataClasses.Public, AiDataClasses.Internal], [])
        {
            ScopeAccess = AiScopeAccessModes.Listed,
        }, default);
        await admin.SetScopeAccessAsync(SiteAdmin, Finance, new AiScopeAccessInput(false, [new AiModelRef(_onPrem, "local-70b")], true), default);
        await admin.CreateConnectionAsync(FinanceAdmin, Finance, new AiConnectionInput("finance-cloud", "demo", ["fin-model"], [AiDataClasses.Internal]), default);
        // Finance's own "smart" replaces the site's for it and its projects; each level has a limit.
        await admin.SetAliasAsync(FinanceAdmin, Finance, "smart", [new AiAliasTarget(_onPrem)], default);
        await admin.SetLimitAsync(SiteAdmin, null, new AiLimitInput(AiLimitKinds.Total, AiPeriods.Month, 100), default);
        await admin.SetLimitAsync(FinanceAdmin, Finance, new AiLimitInput(AiLimitKinds.Person, AiPeriods.Day, 1), default);
        await admin.SetLimitAsync(FinanceAdmin, Budget, new AiLimitInput(AiLimitKinds.Total, AiPeriods.Day, 5), default);

        // Budget, as Finance's admin sees it: the site's model open to it and Finance's connection; the aliases and
        // limits of Finance and the site, not its own.
        AiScopeView budget = await admin.GetScopeViewAsync(FinanceAdmin, Budget, default);
        Assert.Equal(new (string, string, Guid?, bool)[] { ("finance-cloud", "fin-model", Finance, false), ("on-prem", "local-70b", null, false) },
            budget.Models.Select(m => (m.Connection, m.Model, m.SetOnScopeId, m.Own)));
        Assert.Equal(1000m, budget.Models.Single(m => m.Model == "local-70b").Price.Output);
        Assert.Equal(new (string, Guid?)[] { ("public", null), ("smart", Finance) }, budget.AliasesFromAbove.Select(a => (a.Name, a.ScopeId)));
        Assert.Equal(new (Guid?, string)[] { (Finance, AiLimitKinds.Person), (null, AiLimitKinds.Total) }, budget.LimitsFromAbove.Select(l => (l.ScopeId, l.Kind)));
        Assert.Equal((true, false), (budget.MayConnect, budget.ConnectionsKeptByHost));

        // Finance's own: its connection is its own, and it gets only the site's "public".
        AiScopeView finance = await admin.GetScopeViewAsync(FinanceAdmin, Finance, default);
        Assert.True(finance.Models.Single(m => m.Model == "fin-model").Own);
        Assert.Equal(["public"], finance.AliasesFromAbove.Select(a => a.Name));

        // What it names is what a call in it may use.
        Assert.Equal("on-prem", (await Get<IAi>().ResolveAsync("local-70b", Budget, AiDataClasses.Internal)).Connection);
        Assert.Equal(AiRefusalCodes.NoModel, (await Assert.ThrowsAsync<AiRefusedException>(() => Get<IAi>().ResolveAsync("cloud-large", Budget, AiDataClasses.Internal))).Code);

        // The site's admins read it too; Sales' admin and Ben don't.
        Assert.Equal(2, (await admin.GetScopeViewAsync(SiteAdmin, Budget, default)).Models.Count);
        await Assert.ThrowsAsync<Volo.Abp.UserFriendlyException>(() => admin.GetScopeViewAsync(SalesAdmin, Budget, default));
        await Assert.ThrowsAsync<Volo.Abp.UserFriendlyException>(() => admin.GetScopeViewAsync(new AiActor(Ben), Budget, default));
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
