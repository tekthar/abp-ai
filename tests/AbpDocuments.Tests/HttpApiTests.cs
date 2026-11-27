using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AbpDocuments.HttpApi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Microsoft.Extensions.Hosting;
using Volo.Abp;
using Volo.Abp.Modularity;
using Volo.Abp.Security.Claims;
using Volo.Abp.Uow;
using Volo.Abp.EntityFrameworkCore;

namespace AbpDocuments.Tests;

/// <summary>The API under a host's prefix: uploads checked by their bytes, a document one may not read is one there isn't, files served safely.</summary>
public sealed class HttpApiTests : IAsyncLifetime
{
    private readonly string _database = $"abp_documents_http_{Guid.NewGuid():N}";
    private readonly string _files = Path.Combine(Path.GetTempPath(), $"abp-documents-{Guid.NewGuid():N}");
    private string _server = "";
    private WebApplication? _app;
    private HttpClient _http = null!;

    private static readonly Guid Project = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid Member = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid Outsider = Guid.Parse("00000000-0000-0000-0000-0000000000d2");

    public async Task InitializeAsync()
    {
        _server = Environment.GetEnvironmentVariable("ABP_AI_TEST_POSTGRES") is { Length: > 0 } s ? s : "Host=localhost;Port=5432;Username=postgres;Password=postgres";
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration["ConnectionStrings:Default"] = new NpgsqlConnectionStringBuilder(_server) { Database = _database }.ConnectionString;
        builder.Configuration["Tests:Files"] = _files;
        builder.Host.UseAutofac();
        await builder.AddApplicationAsync<HttpTestModule>();
        _app = builder.Build();
        await _app.InitializeApplicationAsync();
        using (IServiceScope scope = _app.Services.CreateScope())
        {
            using IUnitOfWork uow = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin(requiresNew: true);
            TestDb db = await scope.ServiceProvider.GetRequiredService<IDbContextProvider<TestDb>>().GetDbContextAsync();
            await db.Database.EnsureCreatedAsync();
            await uow.CompleteAsync();
        }

        await _app.StartAsync();
        _http = _app.GetTestClient();
        TestRights rights = _app.Services.GetRequiredService<TestRights>();
        rights.Readable[Member] = new DocumentsReach(false, [Project], false);
        rights.Adds[(Member, Project)] = true;
        rights.Manages[(Member, Project)] = true;
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        NpgsqlConnection.ClearAllPools();
        await using NpgsqlConnection admin = new(_server);
        await admin.OpenAsync();
        await using NpgsqlCommand drop = new($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
        if (Directory.Exists(_files))
        {
            Directory.Delete(_files, recursive: true);
        }
    }

    private HttpRequestMessage As(Guid who, HttpMethod method, string path, HttpContent? content = null)
    {
        HttpRequestMessage request = new(method, path) { Content = content };
        request.Headers.Add(HeaderAuth.Header, who.ToString());
        return request;
    }

    private async Task<HttpResponseMessage> Upload(Guid who, string name, byte[] bytes, string? dataClass = null)
    {
        MultipartFormDataContent form = new()
        {
            { new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, "file", name },
            { new StringContent(Project.ToString()), "scopeId" },
        };
        if (dataClass is not null)
        {
            form.Add(new StringContent(dataClass), "dataClass");
        }

        return await _http.SendAsync(As(who, HttpMethod.Post, "/api/files/docs", form));
    }

    [Fact]
    public async Task The_API_checks_uploads_hides_what_one_may_not_read_and_serves_files_safely()
    {
        // Signed in or nothing.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/api/files/docs/settings")).StatusCode);
        JsonElement settings = await (await _http.SendAsync(As(Member, HttpMethod.Get, "/api/files/docs/settings"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("restricted", settings.GetProperty("dataClasses").EnumerateArray().Select(c => c.GetString()));

        // A program named as a PDF is refused; a PDF is taken.
        HttpResponseMessage refused = await Upload(Member, "invoice.pdf", [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00]);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("AbpDocuments:Unsupported", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(Outsider, "a.txt", "x"u8.ToArray())).StatusCode);
        HttpResponseMessage added = await Upload(Member, "terms.pdf", TestFiles.Pdf(("Terms", ["Payment is due in 30 days."])));
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        DocumentInfo doc = (await added.Content.ReadFromJsonAsync<DocumentInfo>(Web))!;
        Assert.Equal(("terms.pdf", "pdf", "internal", DocumentStatuses.Queued), (doc.Name, doc.Kind, doc.DataClass, doc.Status));

        // An outsider gets 404, not 403: the document isn't there for them.
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(As(Outsider, HttpMethod.Get, $"/api/files/docs/{doc.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(As(Outsider, HttpMethod.Get, $"/api/files/docs/{doc.Id}/file"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(As(Outsider, HttpMethod.Delete, $"/api/files/docs/{doc.Id}"))).StatusCode);
        JsonElement none = await (await _http.SendAsync(As(Outsider, HttpMethod.Get, "/api/files/docs?all=true"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, none.GetProperty("total").GetInt32());

        // The PDF inline for the browser's viewer, its type by its bytes; a download as an attachment.
        HttpResponseMessage inline = await _http.SendAsync(As(Member, HttpMethod.Get, $"/api/files/docs/{doc.Id}/file"));
        Assert.Equal("application/pdf", inline.Content.Headers.ContentType!.MediaType);
        Assert.Equal("inline", inline.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", inline.Headers.GetValues("X-Content-Type-Options").Single());
        HttpResponseMessage download = await _http.SendAsync(As(Member, HttpMethod.Get, $"/api/files/docs/{doc.Id}/file?download=true"));
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains("sandbox", download.Headers.GetValues("Content-Security-Policy").Single());

        // Changing the class is the sensitive action: the host's hook ran on it, and not on renaming.
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(As(Member, HttpMethod.Put, $"/api/files/docs/{doc.Id}", JsonContent.Create(new { name = "terms-2026.pdf" })))).StatusCode);
        HttpResponseMessage classify = await _http.SendAsync(As(Member, HttpMethod.Put, $"/api/files/docs/{doc.Id}/class", JsonContent.Create(new { dataClass = "sensitive" })));
        Assert.Equal(HttpStatusCode.OK, classify.StatusCode);
        Assert.Equal("yes", classify.Headers.GetValues("X-Sensitive").Single());

        // Deleted by its manager.
        Assert.Equal(HttpStatusCode.NoContent, (await _http.SendAsync(As(Member, HttpMethod.Delete, $"/api/files/docs/{doc.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.SendAsync(As(Member, HttpMethod.Get, $"/api/files/docs/{doc.Id}"))).StatusCode);
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}

[DependsOn(typeof(DocumentsTestModule))]
public sealed class HttpTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAuthentication(HeaderAuth.SchemeName).AddScheme<AuthenticationSchemeOptions, HeaderAuth>(HeaderAuth.SchemeName, _ => { });
        Configure<DocumentsHttpOptions>(o =>
        {
            o.RoutePrefix = "api/files/docs";
            o.Sensitive = action => action.Filters.Add(new MarkSensitive());
        });
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        IApplicationBuilder app = context.GetApplicationBuilder();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseConfiguredEndpoints();
    }
}

/// <summary>A host's step-up stand-in: says it ran.</summary>
internal sealed class MarkSensitive : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context) => context.HttpContext.Response.Headers["X-Sensitive"] = "yes";

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }
}

/// <summary>Signs a request in as the account its header names.</summary>
internal sealed class HeaderAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string Header = "X-Test-Account";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
        Task.FromResult(Request.Headers.TryGetValue(Header, out var id)
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(AbpClaimTypes.UserId, id.ToString())], SchemeName)), SchemeName))
            : AuthenticateResult.NoResult());
}
