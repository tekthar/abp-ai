using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AbpAi.AspNetCore;
using AbpAi.SampleHost;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AbpAi.Host.Tests;

/// <summary>
/// AbpAi.AspNetCore's admin API on the sample (ADR 0002), served under a prefix of the host's, with a mark of the host's
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

    public Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", $"{GatewayHostTests.Server};Database={_database}");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureServices(s => s.Configure<AiHttpOptions>(o =>
        {
            o.RoutePrefix = Prefix;
            o.OpenApiGroupName = "abpai";
            o.Sensitive = action => action.Filters.Add(new SensitiveMark());
        })));
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
