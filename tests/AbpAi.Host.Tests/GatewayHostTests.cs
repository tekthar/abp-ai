using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AbpAi.Agents;
using AbpAi.Providers;
using AbpAi.SampleHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using AbpAgent;

namespace AbpAi.Host.Tests;

/// <summary>
/// The gateway hosted alone, as Insight hosts it: the sample organization (Finance and Sales over their projects) as
/// scopes, on a database of its own, with two demo connections: "public-cloud" (a provider's cloud; public, internal
/// and aggregates) and "on-prem" (every class). Each "Done when" of the requirements has a test here. Needs Postgres:
/// localhost:5432 (postgres/postgres), or the server ABP_AI_TEST_POSTGRES names.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class GatewayHostTests : IAsyncLifetime
{
    /// <summary>The Postgres server the host tests make their databases on.</summary>
    internal static readonly string Server =
        Environment.GetEnvironmentVariable("ABP_AI_TEST_POSTGRES") is { Length: > 0 } server ? server : "Host=localhost;Port=5432;Username=postgres;Password=postgres";

    // The sample's settings (appsettings.json).
    private static readonly AiActor SiteAdmin = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"));
    private static readonly AiActor FinanceAdmin = new(Guid.Parse("00000000-0000-0000-0000-0000000000f1"));
    private static readonly Guid Ben = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Cy = Guid.Parse("00000000-0000-0000-0000-0000000000c1"); // in the group 2000…01
    private static readonly Guid CysGroup = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Finance = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Budget = Guid.Parse("10000000-0000-0000-0000-000000000011");
    private static readonly Guid Payroll = Guid.Parse("10000000-0000-0000-0000-000000000012");
    private static readonly Guid Sales = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Pipeline = Guid.Parse("10000000-0000-0000-0000-000000000021");

    private readonly string _database = "abp_ai_host_" + Guid.NewGuid().ToString("N")[..12];
    private readonly RecordingNotices _notices = new();
    private readonly AcmeProvider _acme = new();
    private WebApplicationFactory<Program>? _factory;
    private Guid _publicCloud;
    private Guid _onPrem;

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"{Server};Database={_database}");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Singleton<IAiNotices>(_notices));
            s.AddSingleton<IAiProvider>(_acme);
        }));
        _ = _factory.Server; // starts the host, which makes the database

        AiAdminService admin = Get<AiAdminService>();
        // A thousand per million output tokens: a call's cost is its output tokens in thousandths.
        _publicCloud = (await admin.CreateConnectionAsync(SiteAdmin, null, new AiConnectionInput("public-cloud", "demo", ["cloud-large"],
            [AiDataClasses.Public, AiDataClasses.Internal, AiDataClasses.Aggregates]) { Labels = ["provider-cloud"], OutputPrice = 1000, ApiKey = "sk-secret" }, default)).Id;
        _onPrem = (await admin.CreateConnectionAsync(SiteAdmin, null, new AiConnectionInput("on-prem", "demo", ["local-70b"], [.. AiDataClasses.Default])
            { Labels = ["on-premises"], OutputPrice = 1000, StructuredOutput = true }, default)).Id;
        await admin.SetAliasAsync(SiteAdmin, null, "smart", [new AiAliasTarget(_publicCloud), new AiAliasTarget(_onPrem)], default);
        await admin.SetAliasAsync(SiteAdmin, null, "public", [new AiAliasTarget(_publicCloud)], default);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        NpgsqlConnection.ClearAllPools();
        await using NpgsqlConnection connection = new($"{Server};Database=postgres");
        await connection.OpenAsync();
        await using NpgsqlCommand drop = new($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await drop.ExecuteNonQueryAsync();
    }

    // AI1 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_limit_on_a_department_refuses_a_call_in_one_of_its_projects_once_the_departments_total_is_reached()
    {
        await Get<AiAdminService>().SetLimitAsync(FinanceAdmin, Finance, new AiLimitInput(AiLimitKinds.Total, AiPeriods.Month, 0.1m), default);

        // Each call reserves 20 output tokens (0.02) and costs at most that: the department's 0.1 holds five, spread over its projects.
        int answered = 0;
        AiRefusedException? refused = null;
        for (int i = 0; i < 10 && refused is null; i++)
        {
            try
            {
                await Ask(i % 2 == 0 ? Budget : Payroll, Ben);
                answered++;
            }
            catch (AiRefusedException ex)
            {
                refused = ex;
            }
        }

        Assert.NotNull(refused);
        Assert.InRange(answered, 4, 9);
        Assert.Equal(AiRefusalCodes.OverLimit, refused.Code);
        Assert.Equal(("scope", (Guid?)Finance, AiPeriods.Month), (refused.Limit!.Kind, refused.Limit.ScopeId, refused.Limit.Period));
        Assert.Equal(AiPeriods.Of(AiPeriods.Month, DateTime.UtcNow).End, refused.Limit.FreesAt);

        // Both of Finance's projects are refused; Sales isn't.
        await Assert.ThrowsAsync<AiRefusedException>(() => Ask(Budget, Cy));
        await Assert.ThrowsAsync<AiRefusedException>(() => Ask(Payroll, Cy));
        await Ask(Pipeline, Cy);
    }

    [Fact]
    public async Task An_alias_set_on_a_department_applies_to_its_projects_unless_they_set_their_own()
    {
        AiAdminService admin = Get<AiAdminService>();
        IAi ai = Get<IAi>();
        await admin.SetAliasAsync(FinanceAdmin, Finance, "smart", [new AiAliasTarget(_onPrem)], default);

        AiChoice inBudget = await ai.ResolveAsync("smart", Budget, AiDataClasses.Public);
        Assert.Equal(("on-prem", "scope", (Guid?)Finance), (inBudget.Connection, inBudget.Source, inBudget.SetOnScopeId));
        AiChoice inSales = await ai.ResolveAsync("smart", Pipeline, AiDataClasses.Public);
        Assert.Equal(("public-cloud", "site"), (inSales.Connection, inSales.Source));

        await admin.SetAliasAsync(FinanceAdmin, Payroll, "smart", [new AiAliasTarget(_publicCloud)], default);
        Assert.Equal("public-cloud", (await ai.ResolveAsync("smart", Payroll, AiDataClasses.Public)).Connection);

        // A model by its name, when no alias has it.
        AiChoice byName = await ai.ResolveAsync("local-70b", Budget, AiDataClasses.Rows);
        Assert.Equal(("on-prem", "model"), (byName.Connection, byName.Source));
        AiRefusedException none = await Assert.ThrowsAsync<AiRefusedException>(() => ai.ResolveAsync("nonesuch", Budget));
        Assert.Equal(AiRefusalCodes.NoModel, none.Code);

        // A Sales admin can't set Finance's aliases.
        await Assert.ThrowsAsync<Volo.Abp.UserFriendlyException>(() =>
            admin.SetAliasAsync(new AiActor(Guid.Parse("00000000-0000-0000-0000-0000000000e1")), Finance, "smart", [new AiAliasTarget(_onPrem)], default));
    }

    [Fact]
    public async Task Calls_at_the_same_time_cant_go_over_a_limit_together()
    {
        await Get<AiAdminService>().SetLimitAsync(SiteAdmin, Sales, new AiLimitInput(AiLimitKinds.Total, AiPeriods.Day, 0.1m), default);

        Task<bool>[] calls = [.. Enumerable.Range(0, 16).Select(async _ =>
        {
            try
            {
                await Ask(Pipeline, Ben);
                return true;
            }
            catch (AiRefusedException ex) when (ex.Code == AiRefusalCodes.OverLimit)
            {
                return false;
            }
        })];
        bool[] answered = await Task.WhenAll(calls);

        Assert.InRange(answered.Count(a => a), 1, 5);
        decimal spent = await Db(db => db.AiUsage.Where(r => r.ScopePath.Contains(Sales) && r.State == AiUsageStates.Done).SumAsync(r => r.Cost));
        Assert.True(spent <= 0.1m, $"Spent {spent} of 0.1.");
    }

    // AI2 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task One_person_reaching_their_own_limit_doesnt_stop_anyone_else_in_the_same_scope()
    {
        await Get<AiAdminService>().SetLimitAsync(SiteAdmin, null, new AiLimitInput(AiLimitKinds.Person, AiPeriods.Day, 0.05m), default);

        AiRefusedException refused = await AskUntilRefusedAsync(Budget, Ben);
        Assert.Equal(("person", (Guid?)Ben, (Guid?)null), (refused.Limit!.Kind, refused.Limit.AccountId, refused.Limit.ScopeId));
        Assert.Equal(DateTime.UtcNow.Date.AddDays(1), refused.Limit.FreesAt);
        Assert.Contains("daily", refused.Message);

        // Cy, in the same project, still calls; and Ben through an agent counts as Ben.
        await Ask(Budget, Cy);
        Guid agent = Guid.NewGuid();
        AiRefusedException viaAgent = await Assert.ThrowsAsync<AiRefusedException>(() => Ask(Budget, agent, onBehalfOf: Ben));
        Assert.Equal(Ben, viaAgent.Limit!.AccountId);
    }

    [Fact]
    public async Task A_named_account_or_a_group_overrides_what_each_person_may_spend_and_an_agents_own_limit_applies_alongside()
    {
        AiAdminService admin = Get<AiAdminService>();
        await admin.SetLimitAsync(SiteAdmin, null, new AiLimitInput(AiLimitKinds.Person, AiPeriods.Day, 0.05m), default);
        await admin.SetLimitAsync(SiteAdmin, null, new AiLimitInput(AiLimitKinds.Group, AiPeriods.Day, 0.2m, GroupId: CysGroup), default);
        Guid agent = Guid.NewGuid();
        await admin.SetLimitAsync(SiteAdmin, null, new AiLimitInput(AiLimitKinds.Account, AiPeriods.Day, 0.03m, AccountId: agent), default);

        // Cy's group lets them spend past the default.
        for (int i = 0; i < 5; i++)
        {
            await Ask(Budget, Cy);
        }

        // The agent's own limit stops it, though Cy (for whom it runs) has room left.
        AiRefusedException refused = await AskUntilRefusedAsync(Budget, agent, onBehalfOf: Cy);
        Assert.Equal(("account", (Guid?)agent), (refused.Limit!.Kind, refused.Limit.AccountId));
    }

    // AI3 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_call_carrying_rows_never_reaches_a_connection_that_allows_only_aggregates()
    {
        AiScript script = Get<AiScript>();
        script.Clear();

        // "smart" falls back past public-cloud to on-prem for rows.
        AiReply rows = await Ask(Budget, Ben, alias: "smart", dataClass: AiDataClasses.Rows);
        Assert.Equal("on-prem", rows.Connection);
        AiReply totals = await Ask(Budget, Ben, alias: "smart", dataClass: AiDataClasses.Aggregates);
        Assert.Equal("public-cloud", totals.Connection);

        // "public" leads only to public-cloud: rows are refused before they leave, and so is a call that says nothing.
        AiRefusedException refused = await Assert.ThrowsAsync<AiRefusedException>(() => Ask(Budget, Ben, alias: "public", dataClass: AiDataClasses.Rows));
        Assert.Equal(AiRefusalCodes.DataNotAllowed, refused.Code);
        AiRefusedException unstated = await Assert.ThrowsAsync<AiRefusedException>(() => Ask(Budget, Ben, alias: "public", dataClass: null));
        Assert.Equal(AiRefusalCodes.DataNotAllowed, unstated.Code);
        Assert.Equal("on-prem", (await Ask(Budget, Ben, alias: "smart", dataClass: null)).Connection);

        Assert.All(script.Calls, c => Assert.True(c.Target.Connection == "on-prem" || !AiMessages.LastUserText(c.Messages).Contains("rows")));
        Assert.Equal(["on-prem", "public-cloud", "on-prem"], script.Calls.Select(c => c.Target.Connection));
        Assert.Equal(3, await Db(db => db.AiUsage.CountAsync()));
    }

    [Fact]
    public async Task Pointing_an_alias_at_a_public_provider_cant_quietly_widen_what_leaves()
    {
        AiAdminService admin = Get<AiAdminService>();
        await admin.SetAliasAsync(SiteAdmin, null, "ask", [new AiAliasTarget(_onPrem)], default);
        Assert.Equal("on-prem", (await Ask(Budget, Ben, alias: "ask", dataClass: AiDataClasses.Rows)).Connection);

        await admin.SetAliasAsync(SiteAdmin, null, "ask", [new AiAliasTarget(_publicCloud)], default);
        int sent = Get<AiScript>().Calls.Count;
        AiRefusedException refused = await Assert.ThrowsAsync<AiRefusedException>(() => Ask(Budget, Ben, alias: "ask", dataClass: AiDataClasses.Rows));
        Assert.Equal(AiRefusalCodes.DataNotAllowed, refused.Code);
        Assert.Equal(sent, Get<AiScript>().Calls.Count);
    }

    [Fact]
    public async Task A_scopes_own_connection_needs_the_sites_leave_and_may_not_receive_more_than_the_site_lets_it()
    {
        AiAdminService admin = Get<AiAdminService>();
        AiConnectionInput mine = new("finance-cloud", "demo", ["fin-model"], [AiDataClasses.Internal]);
        await Assert.ThrowsAsync<Volo.Abp.UserFriendlyException>(() => admin.CreateConnectionAsync(FinanceAdmin, Finance, mine, default));

        await admin.SetSiteAsync(SiteAdmin, new AiSiteSettings(true, false, 30, [AiDataClasses.Public, AiDataClasses.Internal], []), default);
        await Assert.ThrowsAsync<Volo.Abp.UserFriendlyException>(() =>
            admin.CreateConnectionAsync(FinanceAdmin, Finance, mine with { DataClasses = [AiDataClasses.Rows] }, default));
        AiConnectionInfo created = await admin.CreateConnectionAsync(FinanceAdmin, Finance, mine, default);
        Assert.Equal("finance-cloud", (await Get<IAi>().ResolveAsync("fin-model", Payroll, AiDataClasses.Internal)).Connection);

        // When the site stops letting scopes connect, their connections stop.
        await admin.SetSiteAsync(SiteAdmin, new AiSiteSettings(false, false, 30, [AiDataClasses.Public], []), default);
        Assert.Equal(AiRefusalCodes.NoModel, (await Assert.ThrowsAsync<AiRefusedException>(() => Get<IAi>().ResolveAsync("fin-model", Payroll, AiDataClasses.Internal))).Code);
        Assert.Single(await admin.ListConnectionsAsync(FinanceAdmin, Finance, default), c => c.Id == created.Id);
    }

    [Fact]
    public async Task Keys_are_never_shown_and_prompts_are_kept_only_when_the_site_turns_that_on()
    {
        AiAdminService admin = Get<AiAdminService>();
        AiConnectionInfo cloud = (await admin.ListConnectionsAsync(SiteAdmin, null, default)).Single(c => c.Name == "public-cloud");
        Assert.True(cloud.HasKey);
        Assert.DoesNotContain("sk-secret", JsonSerializer.Serialize(cloud));
        Assert.DoesNotContain("sk-secret", JsonSerializer.Serialize(await Get<IAi>().ResolveAsync("public", null, AiDataClasses.Public)));
        Assert.NotEqual("sk-secret", await Db(db => db.AiConnections.Where(c => c.Id == _publicCloud).Select(c => c.ProtectedKey).SingleAsync()));

        await Ask(Budget, Ben);
        Assert.Equal(0, await Db(db => db.AiPrompts.CountAsync()));
        await admin.SetSiteAsync(SiteAdmin, new AiSiteSettings(false, true, 7, [], []), default);
        AiReply kept = await Ask(Budget, Ben);
        Assert.Equal(1, await Db(db => db.AiPrompts.CountAsync(p => p.Id == kept.UsageId)));

        await admin.SetSiteAsync(SiteAdmin, new AiSiteSettings(false, false, 7, [], []), default);
        await ActivatorUtilities.CreateInstance<AiHousekeeping>(_factory!.Services).RunOnceAsync(default);
        Assert.Equal(0, await Db(db => db.AiPrompts.CountAsync()));
    }

    // AI4 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_departments_admin_sees_their_months_cost_by_project_and_by_purpose_and_nothing_outside_their_department()
    {
        await Ask(Budget, Ben, purpose: "describe-model");
        await Ask(Budget, Ben, purpose: "agent:ask");
        await Ask(Payroll, Cy, purpose: "agent:ask");
        await Ask(Pipeline, Cy, purpose: "agent:ask");
        await Ask(null, Cy, purpose: "explain-change");

        AiUsageService usage = Get<AiUsageService>();
        (DateTime from, DateTime to) = AiPeriods.Of(AiPeriods.Month, DateTime.UtcNow);
        AiUsageReport byProject = await usage.QueryAsync(FinanceAdmin, new AiUsageQuery(from, to, Finance, AiUsageGroups.Scope), default);
        Assert.Equal(["Budget models", "Payroll models"], byProject.Rows.Select(r => r.Title).Order());
        Assert.Equal(3, byProject.Total.Calls);
        Assert.Equal(2, byProject.Rows.Single(r => r.Title == "Budget models").Calls);

        AiUsageReport byPurpose = await usage.QueryAsync(FinanceAdmin, new AiUsageQuery(from, to, Finance, AiUsageGroups.Purpose), default);
        Assert.Equal([("agent:ask", 2), ("describe-model", 1)], byPurpose.Rows.Select(r => (r.Key, r.Calls)).OrderBy(r => r.Key));

        await Assert.ThrowsAsync<Volo.Abp.UserFriendlyException>(() => usage.QueryAsync(FinanceAdmin, new AiUsageQuery(from, to, Sales), default));
        await Assert.ThrowsAsync<Volo.Abp.UserFriendlyException>(() => usage.QueryAsync(FinanceAdmin, new AiUsageQuery(from, to), default));

        string csv = await usage.ExportCsvAsync(FinanceAdmin, new AiUsageQuery(from, to, Finance), default);
        Assert.Equal(4, csv.Trim().Split('\n').Length); // the header and Finance's three
        Assert.DoesNotContain("SALES", csv);
        Assert.DoesNotContain("explain-change", csv);

        // The site sees all of it, by department.
        AiUsageReport site = await usage.QueryAsync(SiteAdmin, new AiUsageQuery(from, to, null, AiUsageGroups.Scope), default);
        Assert.Equal(["(the site)", "Finance", "Sales"], site.Rows.Select(r => r.Title).Order());

        // A person sees their own.
        AiUsageReport cy = await usage.MineAsync(new AiActor(Cy), from, to, AiUsageGroups.Purpose, default);
        Assert.Equal(3, cy.Total.Calls);
    }

    [Fact]
    public async Task Admins_hear_when_a_limit_reaches_its_notice_mark()
    {
        await Get<AiAdminService>().SetLimitAsync(FinanceAdmin, Finance, new AiLimitInput(AiLimitKinds.Total, AiPeriods.Month, 0.1m), default);
        for (int i = 0; i < 3; i++)
        {
            await Ask(Budget, Ben);
        }

        Assert.Empty(_notices.Heard);
        await AskUntilRefusedAsync(Budget, Ben);
        AiLimitNotice notice = Assert.Single(_notices.Heard);
        Assert.Equal((Finance, "scope"), (notice.Limit.ScopeId!.Value, notice.Limit.Kind));
        Assert.True(notice.Limit.Spent >= 0.08m);
    }

    // AI5 and the rest ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_agents_question_set_runs_against_scripted_replies_through_the_gateway()
    {
        using JsonDocument eval = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "evals", "insight-questions.json")));
        AiScript script = Get<AiScript>();
        script.Clear();
        script.Load(eval.RootElement.GetProperty("script").GetRawText());
        AiAgentModels models = new(Get<IAi>(), new ClassFromQuestion(eval.RootElement.GetProperty("questions")));
        Guid agent = Guid.NewGuid();

        foreach (JsonElement q in eval.RootElement.GetProperty("questions").EnumerateArray())
        {
            // The agent's loop, as AbpAgent's runner takes it: a tool's answer goes back until the model answers in words.
            List<object> conversation = [new { role = "user", content = q.GetProperty("question").GetString() }];
            AgentModelReply reply;
            int turns = 0;
            do
            {
                reply = await models.CompleteAsync(new AgentModelRequest(agent, Budget, "smart", JsonSerializer.SerializeToElement(conversation), "You are Ask.", null, 200,
                    "agent:ask", null), default);
                conversation.Add(reply.Message);
                if (reply.Message.GetProperty("stop_reason").GetString() == "tool_use")
                {
                    string id = reply.Message.GetProperty("content").EnumerateArray().First(b => b.GetProperty("type").GetString() == "tool_use").GetProperty("id").GetString()!;
                    conversation.Add(new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = id, content = "North 120, South 80" } } });
                }
            }
            while (reply.Message.GetProperty("stop_reason").GetString() == "tool_use" && ++turns < 5);

            Assert.Contains(q.GetProperty("expect").GetString()!, AiMessages.TextOf(reply.Message), StringComparison.OrdinalIgnoreCase);
        }

        // The question about rows went on premises; the totals and the schema's words may go to the cloud.
        Assert.All(script.Calls.Where(c => AiMessages.LastUserText(c.Messages).Contains("churned")), c => Assert.Equal("on-prem", c.Target.Connection));
        Assert.Contains(script.Calls, c => c.Target.Connection == "public-cloud");
        Assert.Equal(4, await Db(db => db.AiUsage.CountAsync(r => r.Purpose == "agent:ask" && r.AccountId == agent)));
    }

    [Fact]
    public async Task A_stream_gives_the_messages_apis_events_then_the_whole_reply_and_is_recorded()
    {
        Get<AiScript>().Clear();
        Get<AiScript>().EnqueueText("Revenue rose in every region this quarter.");
        List<AiStreamEvent> events = [];
        await foreach (AiStreamEvent e in Get<IAi>().StreamAsync(new AiRequest("smart", AiMessages.User("How did revenue do?"))
                       { ScopeId = Budget, AccountId = Ben, DataClass = AiDataClasses.Aggregates, MaxTokens = 50, Purpose = "chat" }))
        {
            events.Add(e);
        }

        Assert.Equal("message_start", events[0].Type);
        Assert.Contains(events, e => e.Type == "content_block_delta");
        AiReply reply = events[^1].Reply!;
        Assert.Equal(AiStreamEvent.Done, events[^1].Type);
        Assert.Equal("Revenue rose in every region this quarter.", reply.Text);
        AiUsageRecord record = await Db(db => db.AiUsage.SingleAsync(r => r.Id == reply.UsageId));
        Assert.True(record.Streamed);
        Assert.Equal((AiUsageStates.Done, reply.Usage.Cost), (record.State, record.Cost));
        Assert.Equal([Finance, Budget], record.ScopePath);
    }

    [Fact]
    public async Task A_structured_reply_goes_only_to_a_model_that_follows_a_schema()
    {
        Get<AiScript>().Clear();
        Get<AiScript>().EnqueueText("""{"description":"One row per order."}""");
        AiReply reply = await Get<IAi>().CompleteAsync(new AiRequest("smart", AiMessages.User("Describe orders"))
        {
            ScopeId = Budget, AccountId = Ben, DataClass = AiDataClasses.Internal, MaxTokens = 50,
            OutputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { description = new { type = "string" } } }),
        });
        Assert.Equal("on-prem", reply.Connection);
        Assert.Equal("One row per order.", reply.Output!.Value.GetProperty("description").GetString());
    }

    [Fact]
    public async Task System_blocks_and_provider_options_reach_the_provider_and_the_demo_model_sees_the_whole_call()
    {
        AiScript script = Get<AiScript>();
        script.Clear();
        await Get<AiAdminService>().SetSiteAsync(SiteAdmin, new AiSiteSettings(false, true, 7, [], []), default);
        JsonElement blocks = JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "text", text = new string('s', 4000), cache_control = new { type = "ephemeral" } },
            new { type = "text", text = "Answer in one line." },
        });
        AiReply reply = await Get<IAi>().CompleteAsync(new AiRequest("smart", AiMessages.User("Totals by region, please."))
        {
            ScopeId = Budget, AccountId = Ben, DataClass = AiDataClasses.Aggregates, MaxTokens = 20, Purpose = "ask", About = new AiSubject("record", "R-7"),
            System = "Not this: the blocks win.", SystemBlocks = blocks, ProviderOptions = JsonSerializer.SerializeToElement(new { temperature = 0 }),
            Tools = JsonSerializer.SerializeToElement(new[] { new { name = "run_query", input_schema = new { type = "object" } } }),
        });

        AiProviderCall sent = Assert.Single(script.Calls);
        Assert.Equal(blocks.GetRawText(), sent.SystemBlocks!.Value.GetRawText());
        Assert.EndsWith("Answer in one line.", sent.SystemText);
        Assert.Equal(0, sent.ProviderOptions!.Value.GetProperty("temperature").GetInt32());
        Assert.Equal((Ben, "R-7", (Guid?)Budget, "ask"), (sent.Request!.AccountId!.Value, sent.Request.About!.Id, sent.Request.ScopeId, sent.Request.Purpose));
        Assert.Equal(1, sent.Request.Tools!.Value.GetArrayLength());

        // The blocks count as the call's input, and they are what's kept as its system prompt.
        AiUsageRecord record = await Db(db => db.AiUsage.SingleAsync(r => r.Id == reply.UsageId));
        Assert.True(record.InputTokens > 1000, $"{record.InputTokens} tokens in.");
        Assert.Contains("ephemeral", await Db(db => db.AiPrompts.Where(p => p.Id == reply.UsageId).Select(p => p.System).SingleAsync()));

        await Assert.ThrowsAsync<ArgumentException>(() => Get<IAi>().CompleteAsync(AiRequest.Text("smart", "Hi") with
        {
            ProviderOptions = JsonSerializer.SerializeToElement(new[] { 1 }),
        }));
    }

    // Models with what they can do -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_connections_models_are_called_by_their_names_skipped_when_off_and_chosen_for_what_they_can_do()
    {
        AiAdminService admin = Get<AiAdminService>();
        AiScript script = Get<AiScript>();
        script.Clear();
        AiConnectionInput input = new("multi", "demo", ["m-text", "m-vision", "m-off"], [.. AiDataClasses.Default])
        {
            OutputPrice = 1000,
            ModelSettings = new Dictionary<string, AiModelSettings>
            {
                ["m-text"] = new() { Name = "text", Tools = false, ContextTokens = 8000 },
                ["m-vision"] = new() { Name = "vision", Images = true, StructuredOutput = true },
                ["m-off"] = new() { Enabled = false },
            },
        };
        AiConnectionInfo multi = await admin.CreateConnectionAsync(SiteAdmin, null, input, default);
        IAi ai = Get<IAi>();

        AiChoice text = await ai.ResolveAsync("text", Budget, AiDataClasses.Rows);
        Assert.Equal(("m-text", "text", false, false, "model"), (text.Model, text.ModelName, text.Tools, text.Images, text.Source));
        Assert.Equal("m-text", (await ai.ResolveAsync("m-text", Budget, AiDataClasses.Rows)).Model);
        Assert.Equal(AiRefusalCodes.NoModel, (await Assert.ThrowsAsync<AiRefusedException>(() => ai.ResolveAsync("m-off", Budget, AiDataClasses.Rows))).Code);

        // An alias's targets by name are kept by id; a call goes to the first that can do what it needs.
        AiAliasInfo alias = await admin.SetAliasAsync(SiteAdmin, null, "multi", [new AiAliasTarget(multi.Id, "text"), new AiAliasTarget(multi.Id, "vision")], default);
        Assert.Equal(["m-text", "m-vision"], alias.Targets.Select(t => t.Model));
        JsonElement tools = JsonSerializer.SerializeToElement(new[] { new { name = "run_query", input_schema = new { type = "object" } } });
        JsonElement image = JsonSerializer.SerializeToElement(new[]
        {
            new { role = "user", content = new object[] { new { type = "image", source = new { type = "base64", media_type = "image/png", data = "AA==" } } } },
        });
        await Call(AiRequest.Text("multi", "Plain words."));
        await Call(AiRequest.Text("multi", "With tools.") with { Tools = tools });
        await Call(new AiRequest("multi", image));
        await Call(AiRequest.Text("multi", "Shaped.") with { OutputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) });
        Assert.Equal(["m-text", "m-vision", "m-vision", "m-vision"], script.Calls.Select(c => c.Target.Model));

        // When no target can, the call is refused before it leaves.
        await admin.SetAliasAsync(SiteAdmin, null, "multi", [new AiAliasTarget(multi.Id, "text")], default);
        AiRefusedException refused = await Assert.ThrowsAsync<AiRefusedException>(() => Call(AiRequest.Text("multi", "With tools.") with { Tools = tools }));
        Assert.Equal(AiRefusalCodes.NoModel, refused.Code);
        Assert.Contains("takes tools", refused.Message);

        // A model with no settings takes everything: an image still goes to public-cloud, first of "smart".
        Assert.Equal("public-cloud", (await Call(new AiRequest("smart", image) { DataClass = AiDataClasses.Public })).Connection);

        // Changing the connection without settings keeps them, for the models it still offers.
        AiConnectionInfo changed = await admin.UpdateConnectionAsync(SiteAdmin, multi.Id, input with { Models = ["m-text", "m-vision"], ModelSettings = null }, default);
        Assert.Equal(["m-text", "m-vision"], changed.ModelSettings.Keys.Order());
        Assert.Equal(8000, changed.ModelSettings["m-text"].ContextTokens);
        await Assert.ThrowsAsync<Volo.Abp.UserFriendlyException>(() => admin.UpdateConnectionAsync(SiteAdmin, multi.Id, input with
        {
            ModelSettings = new Dictionary<string, AiModelSettings> { ["m-text"] = new() { Name = "m-vision" } },
        }, default));

        // A model's price changing says when: the connection's prices were set when it was made, then one model's.
        DateTime made = changed.ModelSettings["m-text"].PriceSetAt!.Value;
        Assert.Equal(made, changed.ModelSettings["m-vision"].PriceSetAt);
        AiConnectionInfo priced = await admin.UpdateConnectionAsync(SiteAdmin, multi.Id, input with
        {
            ModelSettings = null, ModelPrices = new Dictionary<string, AiPrice> { ["m-vision"] = new(3, 15) },
        }, default);
        Assert.True(priced.ModelSettings["m-vision"].PriceSetAt > made);
        Assert.Equal(made, priced.ModelSettings["m-text"].PriceSetAt);

        Task<AiReply> Call(AiRequest request) => ai.CompleteAsync(request with { ScopeId = Budget, AccountId = Ben, DataClass = request.DataClass ?? AiDataClasses.Rows, MaxTokens = 20 });
    }

    [Fact]
    public async Task Discovery_adds_the_models_a_provider_offers_each_off_until_an_admin_turns_it_on()
    {
        AiAdminService admin = Get<AiAdminService>();
        AiConnectionInput input = new("found", "demo", ["seed"], [.. AiDataClasses.Default]);
        AiConnectionInfo found = await admin.CreateConnectionAsync(SiteAdmin, null, input, default);
        Assert.Equal([DemoProvider.ModelId], await admin.ProviderModelsAsync(SiteAdmin, null, found.Id, default));

        AiConnectionInfo discovered = await admin.DiscoverModelsAsync(SiteAdmin, found.Id, default);
        Assert.Equal(["seed", DemoProvider.ModelId], discovered.Models);
        Assert.False(discovered.ModelSettings[DemoProvider.ModelId].Enabled);
        Assert.False(discovered.ModelSettings.ContainsKey("seed"));
        Assert.Equal(["seed", DemoProvider.ModelId], (await admin.DiscoverModelsAsync(SiteAdmin, found.Id, default)).Models);
        AiRefusedException off = await Assert.ThrowsAsync<AiRefusedException>(() => Get<IAi>().ResolveAsync(DemoProvider.ModelId, Budget, AiDataClasses.Rows));
        Assert.Equal(AiRefusalCodes.NoModel, off.Code);

        await admin.UpdateConnectionAsync(SiteAdmin, found.Id, input with
        {
            Models = discovered.Models, ModelSettings = new Dictionary<string, AiModelSettings> { [DemoProvider.ModelId] = new() { Enabled = true } },
        }, default);
        Assert.Equal("found", (await Get<IAi>().ResolveAsync(DemoProvider.ModelId, Budget, AiDataClasses.Rows)).Connection);

        // Only the site's admins find the site's models.
        await Assert.ThrowsAsync<Volo.Abp.UserFriendlyException>(() => admin.DiscoverModelsAsync(FinanceAdmin, found.Id, default));
    }

    [Fact]
    public async Task A_connection_may_be_to_a_provider_of_the_hosts_own_kind()
    {
        AiAdminService admin = Get<AiAdminService>();
        Assert.True(admin.KeepsConnections);
        AiConnectionInfo bedrock = await admin.CreateConnectionAsync(SiteAdmin, null, new AiConnectionInput("bedrock", AcmeProvider.TheKind, ["acme-large"],
            [.. AiDataClasses.Default]) { ApiKey = "acme-key", OutputPrice = 1000 }, default);
        Assert.Equal(AcmeProvider.TheKind, bedrock.Provider);
        await admin.SetAliasAsync(SiteAdmin, null, "acme", [new AiAliasTarget(bedrock.Id)], default);

        AiReply reply = await Ask(Budget, Ben, alias: "acme", dataClass: AiDataClasses.Rows);
        Assert.Equal("From the host's provider.", reply.Text);
        Assert.Equal(("acme-key", "acme-large"), (Assert.Single(_acme.Calls).Target.ApiKey, _acme.Calls[0].Target.Model));
        Assert.Equal(AcmeProvider.TheKind, await Db(db => db.AiUsage.Where(r => r.Id == reply.UsageId).Select(r => r.Provider).SingleAsync()));
        Assert.Equal(AcmeProvider.TheKind, (await admin.UpdateConnectionAsync(SiteAdmin, bedrock.Id, new AiConnectionInput("bedrock", "ACME:Bedrock", ["acme-large"],
            [.. AiDataClasses.Default]), default)).Provider);
    }

    [Fact]
    public async Task The_api_answers_a_refusal_with_its_code_and_the_limit()
    {
        HttpClient http = _factory!.CreateClient();
        http.DefaultRequestHeaders.Add("X-Account", Ben.ToString());
        HttpResponseMessage ok = await http.PostAsJsonAsync("/api/ai/ask", new AskInput("smart", "Totals?", "FIN-BUDGET", AiDataClasses.Aggregates));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        HttpResponseMessage refused = await http.PostAsJsonAsync("/api/ai/ask", new AskInput("public", "Rows please", "FIN-BUDGET", AiDataClasses.Rows));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(AiRefusalCodes.DataNotAllowed, (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        HttpResponseMessage forbidden = await http.GetAsync("/api/ai/usage?scope=FIN");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    private T Get<T>() where T : notnull => _factory!.Services.GetRequiredService<T>();

    private Task<AiReply> Ask(Guid? scope, Guid account, string alias = "smart", string? dataClass = AiDataClasses.Aggregates, Guid? onBehalfOf = null,
        string purpose = "ask") =>
        Get<IAi>().CompleteAsync(new AiRequest(alias, AiMessages.User("Totals by region, please."))
        {
            ScopeId = scope, AccountId = account, OnBehalfOf = onBehalfOf, DataClass = dataClass, MaxTokens = 20, Purpose = purpose,
        });

    private async Task<AiRefusedException> AskUntilRefusedAsync(Guid scope, Guid account, Guid? onBehalfOf = null)
    {
        for (int i = 0; i < 20; i++)
        {
            try
            {
                await Ask(scope, account, onBehalfOf: onBehalfOf);
            }
            catch (AiRefusedException ex)
            {
                return ex;
            }
        }

        throw new Xunit.Sdk.XunitException("No limit refused twenty calls.");
    }

    private async Task<T> Db<T>(Func<IAiDbContext, Task<T>> read)
    {
        using IServiceScope scope = _factory!.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AiDb>().RunAsync(read);
    }

    private sealed class RecordingNotices : IAiNotices
    {
        public List<AiLimitNotice> Heard { get; } = [];

        public Task LimitNearingAsync(AiLimitNotice notice, CancellationToken cancellationToken)
        {
            lock (Heard)
            {
                Heard.Add(notice);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>The data class each question carries, as Insight's model set would say it.</summary>
    private sealed class ClassFromQuestion(JsonElement questions) : IAiAgentCalls
    {
        public Task<AiAgentCallInfo> DescribeAsync(AgentModelRequest request, CancellationToken cancellationToken)
        {
            string asked = AiMessages.LastUserText(request.Messages);
            string? dataClass = questions.EnumerateArray().FirstOrDefault(q => q.GetProperty("question").GetString() == asked) is { ValueKind: JsonValueKind.Object } q
                ? q.GetProperty("dataClass").GetString()
                : null;
            return Task.FromResult(new AiAgentCallInfo(null, dataClass));
        }
    }
}
