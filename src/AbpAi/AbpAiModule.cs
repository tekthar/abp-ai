using AbpAi.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Volo.Abp.Modularity;
using Volo.Abp.Security;

namespace AbpAi;

/// <summary>
/// AbpAi: the model gateway. A host maps the tables in its own context (<see cref="AiModel.ConfigureAi"/>), replaces
/// <see cref="IAiDbContext"/> with it, and gives its scopes (<see cref="IAiScopes"/>), its admins
/// (<see cref="IAiRights"/>) and, if it has them, groups and a way to tell admins (<see cref="IAiAccounts"/>,
/// <see cref="IAiNotices"/>), and, if it keeps connections of its own, where they are (<see cref="IAiConnectionSource"/>).
/// Everything that calls a model takes <see cref="IAi"/>.
/// </summary>
[DependsOn(typeof(Volo.Abp.EntityFrameworkCore.PostgreSql.AbpEntityFrameworkCorePostgreSqlModule), typeof(AbpSecurityModule))]
public class AbpAiModule : AbpModule
{
    public const string Version = "0.3.0";

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        IServiceCollection services = context.Services;
        services.Configure<AiOptions>(services.GetConfiguration().GetSection(AiOptions.Section));

        services.TryAddTransient<IAi>(sp => sp.GetRequiredService<AiService>());

        // The seams' defaults: a host replaces the ones it has its own of.
        services.TryAddSingleton<IAiScopes, FlatAiScopes>();
        services.TryAddSingleton<IAiRights, NoAiRights>();
        services.TryAddSingleton<IAiAccounts, NoAiGroups>();
        services.TryAddTransient<IAiNotices, LoggedAiNotices>();
        services.TryAddTransient<IAiConnectionSource, StoredAiConnections>();

        // The built-in providers; others plug in by adding an IAiProvider (the last of a kind wins).
        services.AddHttpClient(nameof(AnthropicProvider), c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.AddHttpClient(nameof(OpenAiCompatibleProvider), c => c.Timeout = Timeout.InfiniteTimeSpan);
        services.TryAddEnumerable(ServiceDescriptor.Transient<IAiProvider, AnthropicProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<IAiProvider, OpenAiCompatibleProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<IAiProvider, DemoProvider>());

        // The demo model's script, for tests and evals: empty until something adds to it.
        services.TryAddSingleton<AiScript>();
        services.AddSingleton<IAiDemoResponder>(sp => sp.GetRequiredService<AiScript>());

        services.AddHostedService<AiHousekeeping>();
    }
}
