using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AbpAi.HttpApi;
using AbpAi.SampleHost;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AbpAi.Host.Tests;

/// <summary>
/// AbpAi.HttpApi's admin API on the sample (ADR 0002), served under a prefix of the host's, with a mark of the host's
/// on the sensitive actions and a document of its own: the routes, who may call them, the services' refusals as their
/// statuses, and connections, aliases, limits and where an alias leads, over HTTP. Needs Postgres, as the other host tests.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class AdminApiTests : IAsyncLifetime
{
    private const string Prefix = "/api/admin/ai";
    private static readonly Guid SiteAdmin = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid FinanceAdmin = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
    private static readonly Guid Ben = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Finance = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Budget = Guid.Parse("10000000-0000-0000-0000-000000000011");

    private readonly string _database = "abp_ai_host_" + Guid.NewGuid().ToString("N")[..12];
    private WebApplicationFactory<Program>? _factory;
    private ProbeProvider? _probe;

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"{GatewayHostTests.Server};Database={_database}");
        _probe = new ProbeProvider($"{GatewayHostTests.Server};Database={_database}");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.Configure<AiHttpOptions>(o =>
            {
                o.RoutePrefix = Prefix;
                o.OpenApiGroupName = "abpai";
                o.Sensitive = action => action.Filters.Add(new SensitiveMark());
            });
            s.AddSingleton<IAiProvider>(_probe);
        }));
        _ = _factory.Server; // starts the host, which makes the database
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
    public async Task The_routes_are_under_the_hosts_prefix_and_want_a_signed_in_caller()
    {
        Assert.Equal(HttpStatusCode.OK, (await As(SiteAdmin).GetAsync($"{Prefix}/site")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await As(SiteAdmin).GetAsync("/api/ai/site")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await As(null).GetAsync($"{Prefix}/site")).StatusCode);
    }

    [Fact]
    public async Task What_the_services_refuse_answers_with_its_status()
    {
        // Ben administers nothing; Finance's admin its own scope, not the site.
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Ben).GetAsync($"{Prefix}/site")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(FinanceAdmin).GetAsync($"{Prefix}/site")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await As(FinanceAdmin).GetAsync($"{Prefix}/aliases?scopeId={Finance}")).StatusCode);

        AiSiteSettings site = (await As(SiteAdmin).GetFromJsonAsync<AiSiteSettings>($"{Prefix}/site"))!;
        Assert.Equal(HttpStatusCode.BadRequest, (await As(SiteAdmin).PutAsJsonAsync($"{Prefix}/site", site with { PromptDays = 0 })).StatusCode);
    }

    [Fact]
    public async Task A_sensitive_action_carries_what_the_host_adds_and_a_read_does_not()
    {
        HttpClient admin = As(SiteAdmin);
        HttpResponseMessage read = await admin.GetAsync($"{Prefix}/site");
        AiSiteSettings site = (await read.Content.ReadFromJsonAsync<AiSiteSettings>())!;
        HttpResponseMessage set = await admin.PutAsJsonAsync($"{Prefix}/site", site with { PromptDays = 30 });

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.True(set.Headers.Contains(SensitiveMark.Header));
        Assert.False(read.Headers.Contains(SensitiveMark.Header));
        Assert.Equal(30, (await set.Content.ReadFromJsonAsync<AiSiteSettings>())!.PromptDays);
    }

    [Fact]
    public async Task The_sites_settings_say_who_keeps_the_connections_and_a_set_cant_change_it()
    {
        HttpClient admin = As(SiteAdmin);
        JsonElement read = await admin.GetFromJsonAsync<JsonElement>($"{Prefix}/site");
        Assert.False(read.GetProperty("connectionsKeptByHost").GetBoolean());

        AiSiteSettings site = read.Deserialize<AiSiteSettings>(JsonSerializerOptions.Web)!;
        HttpResponseMessage set = await admin.PutAsJsonAsync($"{Prefix}/site", site with { ConnectionsKeptByHost = true });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.False((await set.Content.ReadFromJsonAsync<AiSiteSettings>())!.ConnectionsKeptByHost);
    }

    [Fact]
    public async Task Connections_aliases_limits_and_where_an_alias_leads_over_http()
    {
        HttpClient admin = As(SiteAdmin);
        HttpResponseMessage made = await admin.PostAsJsonAsync($"{Prefix}/connections",
            new AiConnectionInput("cloud", "demo", ["cloud-large"], [AiDataClasses.Public, AiDataClasses.Internal]));
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        Guid connection = (await made.Content.ReadFromJsonAsync<AiConnectionInfo>())!.Id;

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Prefix}/aliases/smart", new[] { new AiAliasTarget(connection) })).StatusCode);
        Assert.Contains((await admin.GetFromJsonAsync<List<AiAliasInfo>>($"{Prefix}/aliases"))!, a => a.Name == "smart");

        JsonElement leads = await admin.GetFromJsonAsync<JsonElement>($"{Prefix}/resolve/smart?scopeId={Budget}&dataClass={AiDataClasses.Internal}");
        Assert.Equal("cloud-large", leads.GetProperty("model").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Ben).GetAsync($"{Prefix}/resolve/smart")).StatusCode);
        // No model may take rows: the gateway's refusal, with its code.
        HttpResponseMessage refused = await admin.GetAsync($"{Prefix}/resolve/smart?dataClass={AiDataClasses.Rows}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(AiRefusalCodes.DataNotAllowed, (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        HttpResponseMessage limit = await admin.PostAsJsonAsync($"{Prefix}/limits?scopeId={Finance}", new AiLimitInput(AiLimitKinds.Total, AiPeriods.Day, 5));
        Assert.Equal(HttpStatusCode.OK, limit.StatusCode);
        Guid limitId = (await limit.Content.ReadFromJsonAsync<AiLimitInfo>())!.Id;
        Assert.Contains((await admin.GetFromJsonAsync<List<AiLimitInfo>>($"{Prefix}/limits?scopeId={Finance}"))!, l => l.Id == limitId);
        Assert.True((await admin.DeleteAsync($"{Prefix}/limits/{limitId}")).IsSuccessStatusCode);

        // An action that returns a file answers a refusal with its status too, whatever the caller accepts.
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Ben).PostAsync($"{Prefix}/exports/{Guid.NewGuid()}/file", null)).StatusCode);

        Assert.True((await admin.DeleteAsync($"{Prefix}/aliases/smart")).IsSuccessStatusCode);
        Assert.DoesNotContain((await admin.GetFromJsonAsync<List<AiAliasInfo>>($"{Prefix}/aliases"))!, a => a.Name == "smart");
    }

    [Fact]
    public async Task A_scopes_admin_reads_its_view_and_no_one_else_does()
    {
        HttpClient site = As(SiteAdmin);
        HttpResponseMessage made = await site.PostAsJsonAsync($"{Prefix}/connections", new AiConnectionInput("cloud", "demo", ["cloud-large"], [AiDataClasses.Public]));
        Guid connection = (await made.Content.ReadFromJsonAsync<AiConnectionInfo>())!.Id;
        Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"{Prefix}/aliases/fast", new[] { new AiAliasTarget(connection) })).StatusCode);

        // Budget's admins are Finance's: the site's model (every one, while the site doesn't list them) and the site's alias.
        HttpResponseMessage read = await As(FinanceAdmin).GetAsync($"{Prefix}/scopes/{Budget}/view");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        JsonElement view = await read.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement model = Assert.Single(view.GetProperty("models").EnumerateArray());
        Assert.Equal(("cloud-large", false), (model.GetProperty("model").GetString(), model.GetProperty("own").GetBoolean()));
        Assert.Equal("fast", Assert.Single(view.GetProperty("aliasesFromAbove").EnumerateArray()).GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await As(Ben).GetAsync($"{Prefix}/scopes/{Budget}/view")).StatusCode);
    }

    [Fact]
    public async Task A_try_answers_what_the_model_said_and_a_refusal_answers_422_with_its_code_and_limit()
    {
        HttpClient site = As(SiteAdmin);
        HttpResponseMessage made = await site.PostAsJsonAsync($"{Prefix}/connections", new AiConnectionInput("cloud", "demo", ["cloud-large"], [AiDataClasses.Public]));
        Guid connection = (await made.Content.ReadFromJsonAsync<AiConnectionInfo>())!.Id;
        Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"{Prefix}/aliases/fast", new[] { new AiAliasTarget(connection) })).StatusCode);

        // Finance's admin tries the site's alias in a project of theirs; it isn't a sensitive action.
        HttpClient finance = As(FinanceAdmin);
        HttpResponseMessage tried = await finance.PostAsJsonAsync($"{Prefix}/try", new AiTryInput("fast", "Say hello.", Budget, AiDataClasses.Public));
        Assert.Equal(HttpStatusCode.OK, tried.StatusCode);
        Assert.False(tried.Headers.Contains(SensitiveMark.Header));
        JsonElement reply = await tried.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("cloud-large", "cloud"), (reply.GetProperty("model").GetString(), reply.GetProperty("connection").GetString()));
        Assert.False(string.IsNullOrEmpty(reply.GetProperty("text").GetString()));
        Assert.True(reply.GetProperty("usage").GetProperty("outputTokens").GetInt32() > 0);

        // Data the alias's model may not take, and a limit reached: 422, the gateway's code, and the limit.
        HttpResponseMessage rows = await finance.PostAsJsonAsync($"{Prefix}/try", new AiTryInput("fast", "Rows", Budget, AiDataClasses.Rows));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rows.StatusCode);
        Assert.Equal(AiRefusalCodes.DataNotAllowed, (await rows.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        await site.PostAsJsonAsync($"{Prefix}/limits?scopeId={Finance}", new AiLimitInput(AiLimitKinds.Total, AiPeriods.Day, 1) { Measure = AiLimitMeasures.Requests });
        HttpResponseMessage over = await finance.PostAsJsonAsync($"{Prefix}/try", new AiTryInput("fast", "Again", Budget, AiDataClasses.Public));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        JsonElement refusal = await over.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((AiRefusalCodes.OverLimit, Finance), (refusal.GetProperty("code").GetString(), refusal.GetProperty("limit").GetProperty("scopeId").GetGuid()));

        // Only those who administer the scope; nothing to say is the services' 400.
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Ben).PostAsJsonAsync($"{Prefix}/try", new AiTryInput("fast", "Hi", Budget))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await finance.PostAsJsonAsync($"{Prefix}/try", new AiTryInput("fast", "", Budget))).StatusCode);
    }

    [Fact]
    public async Task An_alias_is_named_in_lowercase_and_leads_only_to_a_model_thats_on()
    {
        HttpClient site = As(SiteAdmin);
        HttpResponseMessage made = await site.PostAsJsonAsync($"{Prefix}/connections", new AiConnectionInput("cloud", "demo", ["cloud-large", "cloud-old"], [AiDataClasses.Public])
        {
            ModelSettings = new Dictionary<string, AiModelSettings> { ["cloud-old"] = new() { Name = "Old", Enabled = false } },
        });
        Guid connection = (await made.Content.ReadFromJsonAsync<AiConnectionInfo>())!.Id;

        // Kept in lowercase, as the router is asked for it.
        HttpResponseMessage upper = await site.PutAsJsonAsync($"{Prefix}/aliases/%20Smart-2%20", new[] { new AiAliasTarget(connection) });
        Assert.Equal(HttpStatusCode.OK, upper.StatusCode);
        Assert.Equal("smart-2", (await upper.Content.ReadFromJsonAsync<AiAliasInfo>())!.Name);

        // Letters, digits and dashes, from a letter, at most 40.
        foreach (string bad in new[] { "two%20words", "2fast", "-fast", "fast_lane", "fast.lane", new string('a', AiAdminService.AliasNameLength + 1) })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await site.PutAsJsonAsync($"{Prefix}/aliases/{bad}", new[] { new AiAliasTarget(connection) })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"{Prefix}/aliases/{new string('a', AiAdminService.AliasNameLength)}", new[] { new AiAliasTarget(connection) })).StatusCode);

        // A model that's off, by its id or its name, isn't a target; nor is a connection whose models are all off.
        Assert.Equal(HttpStatusCode.BadRequest, (await site.PutAsJsonAsync($"{Prefix}/aliases/old", new[] { new AiAliasTarget(connection, "cloud-old") })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await site.PutAsJsonAsync($"{Prefix}/aliases/old", new[] { new AiAliasTarget(connection, "Old") })).StatusCode);
        HttpResponseMessage off = await site.PostAsJsonAsync($"{Prefix}/connections", new AiConnectionInput("dark", "demo", ["dark-1"], [AiDataClasses.Public])
        {
            ModelSettings = new Dictionary<string, AiModelSettings> { ["dark-1"] = new() { Enabled = false } },
        });
        Guid dark = (await off.Content.ReadFromJsonAsync<AiConnectionInfo>())!.Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await site.PutAsJsonAsync($"{Prefix}/aliases/dark", new[] { new AiAliasTarget(dark) })).StatusCode);
        Assert.DoesNotContain((await site.GetFromJsonAsync<List<AiAliasInfo>>($"{Prefix}/aliases"))!, a => a.Name is "old" or "dark");

        // A fallback switched off after it was set stays when the alias is set again, as a screen sends it back unchanged.
        HttpResponseMessage spareMade = await site.PostAsJsonAsync($"{Prefix}/connections", new AiConnectionInput("spare", "demo", ["spare-1"], [AiDataClasses.Public]));
        Guid spare = (await spareMade.Content.ReadFromJsonAsync<AiConnectionInfo>())!.Id;
        Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"{Prefix}/aliases/both", new[] { new AiAliasTarget(connection, "cloud-large"), new AiAliasTarget(spare) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"{Prefix}/connections/{spare}", new AiConnectionInput("spare", "demo", ["spare-1"], [AiDataClasses.Public])
        {
            ModelSettings = new Dictionary<string, AiModelSettings> { ["spare-1"] = new() { Enabled = false } },
        })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"{Prefix}/aliases/both", new[] { new AiAliasTarget(connection), new AiAliasTarget(spare) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await site.PutAsJsonAsync($"{Prefix}/aliases/both", new[] { new AiAliasTarget(connection), new AiAliasTarget(spare), new AiAliasTarget(connection, "cloud-old") })).StatusCode);
    }

    [Fact]
    public async Task A_call_to_a_provider_holds_no_transaction_open_and_a_connection_is_saved_before_its_test()
    {
        HttpClient site = As(SiteAdmin);

        // Saved with a test: the connection is kept before the test's call, which sees it, under its new name after a change.
        HttpResponseMessage made = await site.PostAsJsonAsync($"{Prefix}/connections",
            new AiConnectionInput("probe", ProbeProvider.TheKind, ["probe-1"], [AiDataClasses.Public]) { Test = true });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        AiConnectionInfo connection = (await made.Content.ReadFromJsonAsync<AiConnectionInfo>())!;
        Assert.True(connection.Test?.Ok, connection.Test?.Reason);
        HttpResponseMessage changed = await site.PutAsJsonAsync($"{Prefix}/connections/{connection.Id}",
            new AiConnectionInput("probe-2", ProbeProvider.TheKind, ["probe-1"], [AiDataClasses.Public]) { Test = true });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        // A test on its own, and a try through an alias.
        Assert.Equal(HttpStatusCode.OK, (await site.PostAsync($"{Prefix}/connections/{connection.Id}/test", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.PutAsJsonAsync($"{Prefix}/aliases/probed", new[] { new AiAliasTarget(connection.Id) })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.PostAsJsonAsync($"{Prefix}/try", new AiTryInput("probed", "Say OK.", null, AiDataClasses.Public))).StatusCode);

        ProbeSight[] seen = [.. _probe!.Seen];
        Assert.Equal(["connection-test", "connection-test", "connection-test", AiAdminService.TryPurpose], seen.Select(s => s.Purpose));
        Assert.All(seen, s => Assert.Equal(0, s.InTransaction));
        Assert.Equal(["probe"], seen[0].Connections);
        Assert.Equal(["probe-2"], seen[1].Connections);
    }

    [Fact]
    public void The_api_is_in_its_own_openapi_group()
    {
        IReadOnlyList<ApiDescriptionGroup> groups = _factory!.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items;
        ApiDescriptionGroup group = Assert.Single(groups, g => g.GroupName == "abpai");
        Assert.Contains(group.Items, d => d.RelativePath == "api/admin/ai/site" && d.HttpMethod == "PUT");
        Assert.Contains(group.Items, d => d.RelativePath == "api/admin/ai/exports/{id}/file");
    }

    private HttpClient As(Guid? account)
    {
        HttpClient http = _factory!.CreateClient();
        if (account is { } id)
        {
            http.DefaultRequestHeaders.Add("X-Account", id.ToString());
        }

        return http;
    }

    /// <summary>The host's addition on a sensitive action, in these tests: a header on its answer.</summary>
    private sealed class SensitiveMark : IActionFilter
    {
        public const string Header = "X-Sensitive";

        public void OnActionExecuting(ActionExecutingContext context) => context.HttpContext.Response.Headers[Header] = "yes";

        public void OnActionExecuted(ActionExecutedContext context)
        {
        }
    }
}
