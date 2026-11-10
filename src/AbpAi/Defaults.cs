using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AbpAi;

/// <summary>One level of scopes: every scope's parent is the site.</summary>
public sealed class FlatAiScopes : IAiScopes
{
    public Task<Guid?> ParentOfAsync(Guid scopeId, CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);
}

/// <summary>No one administers anything until the host says who does.</summary>
public sealed class NoAiRights : IAiRights
{
    public Task<bool> IsSiteAdminAsync(AiActor actor, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<bool> AdministersScopeAsync(AiActor actor, Guid scopeId, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>No groups.</summary>
public sealed class NoAiGroups : IAiAccounts
{
    public Task<IReadOnlyList<Guid>> GroupsOfAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([]);
}

/// <summary>Writes a limit nearing to the log.</summary>
public sealed class LoggedAiNotices(ILogger<LoggedAiNotices> logger) : IAiNotices
{
    public Task LimitNearingAsync(AiLimitNotice notice, CancellationToken cancellationToken)
    {
        logger.LogWarning("AI limit {LimitId} ({Kind}, {Period}) on {Scope} is at {Spent} of {Amount}; it frees at {FreesAt:u}.", notice.Limit.LimitId,
            notice.Limit.Kind, notice.Limit.Period, notice.Limit.ScopeId?.ToString() ?? "the site", notice.Limit.Spent, notice.Limit.Amount, notice.Limit.FreesAt);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Lets go reservations no one settled (a server stopped mid-call), and deletes kept prompts past the site's period
/// (all of them once the site stops keeping prompts).
/// </summary>
public sealed class AiHousekeeping(IServiceScopeFactory scopes, IOptions<AiOptions> options, ILogger<AiHousekeeping> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(options.Value.HousekeepingEvery);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The AI gateway's housekeeping failed; it tries again later.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using IServiceScope scope = scopes.CreateScope();
        AiDb aiDb = scope.ServiceProvider.GetRequiredService<AiDb>();
        DateTime stale = DateTime.UtcNow - options.Value.ReservationTimeout;
        await aiDb.RunAsync(async db =>
        {
            await db.AiUsage.Where(r => r.State == AiUsageStates.Reserved && r.At < stale)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.State, AiUsageStates.Abandoned).SetProperty(r => r.Cost, 0m), ct);
            AiSite? site = await db.AiSites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == AiSite.TheId, ct);
            DateTime keepSince = site is { KeepPrompts: true } ? DateTime.UtcNow.AddDays(-site.PromptDays) : DateTime.MaxValue;
            await db.AiPrompts.Where(p => p.At < keepSince).ExecuteDeleteAsync(ct);
        }, requiresNew: true, cancellationToken: ct);
    }
}
