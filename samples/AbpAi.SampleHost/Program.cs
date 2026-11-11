using AbpAi.SampleHost;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Uow;

// A host of AbpAi alone: an organization's departments over its projects as scopes, the demo model as its provider.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Host.UseAutofac();
await builder.AddApplicationAsync<SampleHostModule>();

WebApplication app = builder.Build();
await app.InitializeApplicationAsync();
await MigrateAsync(app.Services);
await app.RunAsync();

// The sample makes its tables with its migrations (Migrations/), as a host does: the gateway's tables are in the host's
// own migrations (dotnet ef migrations add, after ConfigureAi), run on start or in its deploy. AbpAi020 makes 0.2.0's
// tables; each later one is what a version adds.
static async Task MigrateAsync(IServiceProvider services)
{
    using IServiceScope scope = services.CreateScope();
    using IUnitOfWork uow = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>().Begin();
    SampleDbContext db = await scope.ServiceProvider.GetRequiredService<IDbContextProvider<SampleDbContext>>().GetDbContextAsync();
    await db.Database.MigrateAsync();
    await uow.CompleteAsync();
}

/// <summary>The program, for the tests' WebApplicationFactory.</summary>
public partial class Program;
