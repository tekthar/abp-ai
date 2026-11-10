using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Volo.Abp;
using Volo.Abp.AspNetCore.ExceptionHandling;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Autofac;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Modularity;

namespace AbpAi.SampleHost;

/// <summary>
/// The gateway's module and ABP's: all a host needs. The sample's context holds the gateway's tables; it gives its
/// scopes (departments over projects), its admins and its groups, and keeps the defaults for the rest.
/// </summary>
[DependsOn(typeof(AbpAutofacModule), typeof(AbpAspNetCoreMvcModule), typeof(AbpAiModule))]
public class SampleHostModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<SampleDbContext>(options => options.ReplaceDbContext<IAiDbContext>());
        Configure<AbpDbContextOptions>(options => options.UseNpgsql());
        context.Services.Replace(ServiceDescriptor.Singleton<IAiScopes>(sp => sp.GetRequiredService<SampleOrg>()));
        context.Services.Replace(ServiceDescriptor.Singleton<IAiRights>(sp => sp.GetRequiredService<SampleOrg>()));
        context.Services.Replace(ServiceDescriptor.Singleton<IAiAccounts>(sp => sp.GetRequiredService<SampleOrg>()));
        Configure<AbpExceptionHttpStatusCodeOptions>(options =>
        {
            options.Map(AiErrorCodes.Forbidden, HttpStatusCode.Forbidden);
            options.Map(AiErrorCodes.Invalid, HttpStatusCode.BadRequest);
        });
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        IApplicationBuilder app = context.GetApplicationBuilder();
        app.UseRouting();
        app.UseConfiguredEndpoints();
    }
}

/// <summary>The sample's context: the gateway's tables, in schema <c>ai</c>.</summary>
[ConnectionStringName("Default")]
[ReplaceDbContext(typeof(IAiDbContext))]
public class SampleDbContext(DbContextOptions<SampleDbContext> options) : AbpDbContext<SampleDbContext>(options), IAiDbContext
{
    public DbSet<AiConnection> AiConnections => Set<AiConnection>();
    public DbSet<AiAlias> AiAliases => Set<AiAlias>();
    public DbSet<AiLimit> AiLimits => Set<AiLimit>();
    public DbSet<AiUsageRecord> AiUsage => Set<AiUsageRecord>();
    public DbSet<AiPromptRecord> AiPrompts => Set<AiPromptRecord>();
    public DbSet<AiSite> AiSites => Set<AiSite>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ConfigureAi();
    }
}

/// <summary>A scope in the sample's settings: a department (no parent) or a project under one.</summary>
public sealed class SampleScope
{
    public Guid Id { get; set; }
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public Guid? Parent { get; set; }
    public List<Guid> Admins { get; set; } = [];
}

/// <summary>
/// The sample's organization, from its settings: departments over projects (the scopes), who administers the site and
/// each department, and groups. Insight's would be its model sets and org units, or AbpGuard's org tree.
/// </summary>
public sealed class SampleOrg(IConfiguration configuration) : IAiScopes, IAiRights, IAiAccounts, ISingletonDependency
{
    private readonly List<Guid> _siteAdmins = configuration.GetSection("Sample:SiteAdmins").Get<List<Guid>>() ?? [];
    private readonly Dictionary<Guid, SampleScope> _scopes = (configuration.GetSection("Sample:Scopes").Get<List<SampleScope>>() ?? []).ToDictionary(s => s.Id);
    private readonly Dictionary<Guid, List<Guid>> _groups = configuration.GetSection("Sample:Groups").GetChildren()
        .ToDictionary(c => Guid.Parse(c.Key), c => c.Get<List<Guid>>() ?? []);

    public Guid IdOf(string key) => _scopes.Values.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase))?.Id
        ?? throw new UserFriendlyException($"There's no scope {key}.", AiErrorCodes.Invalid);

    public Task<Guid?> ParentOfAsync(Guid scopeId, CancellationToken cancellationToken) =>
        Task.FromResult(_scopes.TryGetValue(scopeId, out SampleScope? scope) ? scope.Parent : null);

    public Task<IReadOnlyDictionary<Guid, AiScopeInfo>> GetManyAsync(IEnumerable<Guid> scopeIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, AiScopeInfo>>(scopeIds.Where(_scopes.ContainsKey).Distinct()
            .ToDictionary(id => id, id => new AiScopeInfo(id, _scopes[id].Key, _scopes[id].Title)));

    public Task<bool> IsSiteAdminAsync(AiActor actor, CancellationToken cancellationToken) => Task.FromResult(_siteAdmins.Contains(actor.Id));

    public Task<bool> AdministersScopeAsync(AiActor actor, Guid scopeId, CancellationToken cancellationToken) =>
        Task.FromResult(_scopes.TryGetValue(scopeId, out SampleScope? scope) && scope.Admins.Contains(actor.Id));

    public Task<IReadOnlyList<Guid>> GroupsOfAsync(Guid accountId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. _groups.Where(g => g.Value.Contains(accountId)).Select(g => g.Key)]);
}
