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

    [Fact]
    public void A_scopes_view_has_the_site_models_its_access_opens_and_the_connections_set_on_it_and_above_it()
    {
        Guid budget = Guid.NewGuid(), finance = Guid.NewGuid();
        AiScopeChain chain = new([budget, finance]);
        AiConnectionDescriptor cloud = new(Guid.NewGuid(), null, "cloud", "demo")
        {
            Models = ["cloud-large", "cloud-small"], OutputPrice = 15, StructuredOutput = true,
            ModelSettings = new Dictionary<string, AiModelSettings> { ["cloud-small"] = new() { Name = "small", Tools = false, ContextTokens = 8000 } },
        };
        AiConnectionDescriptor onPrem = new(Guid.NewGuid(), null, "on-prem", "demo") { Models = ["local-70b"] };
        AiConnectionDescriptor off = new(Guid.NewGuid(), null, "off", "demo") { Models = ["old"], Enabled = false };
        AiConnectionDescriptor finances = new(Guid.NewGuid(), finance, "finance-own", "demo") { Models = ["fin-model"] };
        AiConnectionDescriptor budgets = new(Guid.NewGuid(), budget, "budget-own", "demo")
        {
            Models = ["bud-model", "bud-off"], ModelSettings = new Dictionary<string, AiModelSettings> { ["bud-off"] = new() { Enabled = false } },
        };
        AiConnectionDescriptor sales = new(Guid.NewGuid(), Guid.NewGuid(), "sales-own", "demo") { Models = ["sales-model"] };
        AiConnectionDescriptor[] connections = [cloud, onPrem, off, finances, budgets, sales];

        // The site lists access; Finance's opens cloud's small model and lets it connect. Models that are off, and
        // connections off or set elsewhere, aren't in it; the nearest connections come first.
        AiSite listed = new(AiSite.TheId) { ScopeAccess = AiScopeAccessModes.Listed, ScopesMayConnect = true };
        AiScopeAccess access = new(finance) { SiteModels = [new AiModelRef(cloud.Id, "cloud-small")], MayConnect = true };
        AiScopeView view = AiAdminService.ViewOf(chain, listed, access, connections, [], [], keptByHost: false);
        Assert.Equal(new (string, string, Guid?, bool)[] { ("budget-own", "bud-model", budget, true), ("finance-own", "fin-model", finance, false), ("cloud", "cloud-small", null, false) },
            view.Models.Select(m => (m.Connection, m.Model, m.SetOnScopeId, m.Own)));
        Assert.True(view.MayConnect);

        // A model's settings as the router reads them: its own, its schema rule its connection's; none, and it takes everything.
        AiScopeModel small = view.Models.Single(m => m.Model == "cloud-small");
        Assert.Equal(("small", true, false, false, (bool?)true, (int?)8000, 15m), (small.Settings.Name, small.Settings.Enabled, small.Settings.Tools, small.Settings.Images,
            small.Settings.StructuredOutput, small.Settings.ContextTokens, small.Price.Output));
        AiModelSettings bud = view.Models.Single(m => m.Model == "bud-model").Settings;
        Assert.Equal(("bud-model", true, true, (bool?)false), (bud.Name, bud.Tools, bud.Images, bud.StructuredOutput));

        // Access that opens every site model, but not connecting: the site's, and no connections of the scopes'.
        AiScopeView all = AiAdminService.ViewOf(chain, listed, new AiScopeAccess(finance) { AllSiteModels = true }, connections, [], [], false);
        Assert.Equal(["cloud-large", "cloud-small", "local-70b"], all.Models.Select(m => m.Model));
        Assert.False(all.MayConnect);

        // Open (the default, and a site with no settings yet): every site model; no access at all when listed: nothing.
        Assert.Equal(3, AiAdminService.ViewOf(chain, null, null, connections, [], [], false).Models.Count);
        Assert.Empty(AiAdminService.ViewOf(chain, listed, null, connections, [], [], false).Models);
    }

    [Fact]
    public void A_scopes_view_has_the_nearest_alias_of_each_name_above_it_that_it_doesnt_set_and_the_limits_above_it()
    {
        Guid budget = Guid.NewGuid(), finance = Guid.NewGuid(), elsewhere = Guid.NewGuid();
        AiScopeChain chain = new([budget, finance]);
        AiAlias[] aliases = [Alias(null, "smart"), Alias(null, "fast"), Alias(null, "embed"), Alias(finance, "fast"), Alias(budget, "smart"), Alias(elsewhere, "other")];
        AiLimit[] limits =
        [
            new(Guid.NewGuid(), null, AiLimitKinds.Total, AiPeriods.Month, 100), new(Guid.NewGuid(), finance, AiLimitKinds.Person, AiPeriods.Day, 1),
            new(Guid.NewGuid(), budget, AiLimitKinds.Total, AiPeriods.Day, 5), new(Guid.NewGuid(), elsewhere, AiLimitKinds.Total, AiPeriods.Day, 5),
        ];

        AiScopeView view = AiAdminService.ViewOf(chain, null, null, [], aliases, limits, keptByHost: true);
        Assert.Equal(new (string, Guid?)[] { ("embed", null), ("fast", finance) }, view.AliasesFromAbove.Select(a => (a.Name, a.ScopeId)));
        Assert.Equal(new (Guid?, string)[] { (finance, AiLimitKinds.Person), (null, AiLimitKinds.Total) }, view.LimitsFromAbove.Select(l => (l.ScopeId, l.Kind)));
        Assert.True(view.ConnectionsKeptByHost);
        Assert.Throws<ArgumentException>(() => AiAdminService.ViewOf(AiScopeChain.Site, null, null, [], [], [], false));
    }

    private static AiAlias Alias(Guid? scope, string name) => new(Guid.NewGuid(), scope, name) { Targets = [new AiAliasTarget(Guid.NewGuid())] };

    [Fact]
    public async Task A_try_is_a_call_as_the_admin_with_its_own_purpose_for_those_who_administer_the_scope()
    {
        Guid scope = Guid.NewGuid();
        RecordingAi ai = new();
        AiAdminService admin = Admin(new HostConnections(), new ScopeAdmin(Ben, scope), ai);

        AiTryReply reply = await admin.TryAsync(new AiActor(Ben), new AiTryInput(" smart ", "Say hello.", scope, AiDataClasses.Internal), default);
        Assert.Equal(("ok", "demo-model", "cloud", 4), (reply.Text, reply.Model, reply.Connection, reply.Usage.OutputTokens));
        AiRequest sent = Assert.Single(ai.Sent);
        Assert.Equal(("smart", (Guid?)scope, (Guid?)Ben, (Guid?)null, AiDataClasses.Internal, AiAdminService.TryPurpose),
            (sent.Alias, sent.ScopeId, sent.AccountId, sent.OnBehalfOf, sent.DataClass, sent.Purpose));
        Assert.Equal("Say hello.", AiMessages.LastUserText(sent.Messages));

        // Someone who doesn't administer it, or the site, may not; a try with nothing to say is refused before it's made.
        Assert.Equal(AiErrorCodes.Forbidden, (await Assert.ThrowsAsync<Volo.Abp.UserFriendlyException>(() =>
            admin.TryAsync(new AiActor(Guid.NewGuid()), new AiTryInput("smart", "Hi", scope), default))).Code);
        Assert.Equal(AiErrorCodes.Forbidden, (await Assert.ThrowsAsync<Volo.Abp.UserFriendlyException>(() =>
            admin.TryAsync(new AiActor(Ben), new AiTryInput("smart", "Hi"), default))).Code);
        Assert.Equal(AiErrorCodes.Invalid, (await Assert.ThrowsAsync<Volo.Abp.UserFriendlyException>(() =>
            admin.TryAsync(new AiActor(Ben), new AiTryInput("smart", " ", scope), default))).Code);

        // The gateway's refusal is the caller's to answer.
        ai.Refuse = new AiRefusedException(AiRefusalCodes.DataNotAllowed, "No model may take it.");
        Assert.Equal(AiRefusalCodes.DataNotAllowed, (await Assert.ThrowsAsync<AiRefusedException>(() =>
            admin.TryAsync(new AiActor(Ben), new AiTryInput("smart", "Hi", scope), default))).Code);
        Assert.Single(ai.Sent);
    }

    /// <summary>The admin service with what its database-free parts need; the rest is left out.</summary>
    private static AiAdminService Admin(IAiConnectionSource source, IAiRights? rights = null, IAi? ai = null) =>
        new(null!, rights ?? new NoAiRights(), new AiScopeChains(new FlatAiScopes(), Options.Create(new AiOptions())), new AiDataRules(Options.Create(new AiOptions())),
            [], null!, source, Options.Create(new AiOptions()), ai!);

    /// <summary>One person administers one scope.</summary>
    private sealed class ScopeAdmin(Guid person, Guid scope) : IAiRights
    {
        public Task<bool> IsSiteAdminAsync(AiActor actor, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> AdministersScopeAsync(AiActor actor, Guid scopeId, CancellationToken cancellationToken) =>
            Task.FromResult(actor.Id == person && scopeId == scope);
    }

    /// <summary>A gateway that keeps each call and answers it, or refuses it when told to.</summary>
    private sealed class RecordingAi : IAi
    {
        public List<AiRequest> Sent { get; } = [];

        public AiRefusedException? Refuse { get; set; }

        public Task<AiReply> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            if (Refuse is not null)
            {
                throw Refuse;
            }

            Sent.Add(request);
            return Task.FromResult(new AiReply(AiMessages.AssistantText("ok"), "demo-model", new AiUsage(3, 4, 0.5m)) { Connection = "cloud" });
        }

        public IAsyncEnumerable<AiStreamEvent> StreamAsync(AiRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AiChoice> ResolveAsync(string alias, Guid? scopeId = null, string? dataClass = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

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
