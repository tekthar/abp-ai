using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.Security.Encryption;

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

/// <summary>
/// The connections AbpAi keeps itself, in its <c>connections</c> table, set through <see cref="AiAdminService"/>; their keys
/// are encrypted with the site's string encryption and decrypted only as a call goes out.
/// </summary>
public class StoredAiConnections(AiDb aiDb, IStringEncryptionService encryption) : IAiConnectionSource
{
    public async Task<IReadOnlyList<AiConnectionDescriptor>> ListAsync(IReadOnlyCollection<Guid> scopeIds, CancellationToken cancellationToken)
    {
        List<Guid> ids = [.. scopeIds];
        List<AiConnection> found = await aiDb.RunAsync(db => db.AiConnections.AsNoTracking().Where(c => c.ScopeId == null || ids.Contains(c.ScopeId.Value))
            .ToListAsync(cancellationToken), cancellationToken: cancellationToken);
        return [.. found.Select(Describe)];
    }

    public async Task<string?> KeyAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        string? key = await aiDb.RunAsync(db => db.AiConnections.AsNoTracking().Where(c => c.Id == connectionId).Select(c => c.ProtectedKey).FirstOrDefaultAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return key is { Length: > 0 } ? encryption.Decrypt(key) : null;
    }

    /// <summary>A stored connection as the gateway reads it.</summary>
    public static AiConnectionDescriptor Describe(AiConnection c) => new(c.Id, c.ScopeId, c.Name, c.Provider)
    {
        Endpoint = c.Endpoint,
        Settings = c.Settings,
        Models = c.Models,
        ModelSettings = c.ModelSettings,
        DataClasses = c.DataClasses,
        Labels = c.Labels,
        InputPrice = c.InputPrice,
        OutputPrice = c.OutputPrice,
        ModelPrices = c.ModelPrices,
        CacheWritePrice = c.CacheWritePrice,
        CacheReadPrice = c.CacheReadPrice,
        StructuredOutput = c.StructuredOutput,
        Enabled = c.Enabled,
        HasKey = c.ProtectedKey is not null,
        IdleTimeout = c.IdleTimeoutSeconds is { } idle ? TimeSpan.FromSeconds(idle) : null,
        TotalTimeout = c.TotalTimeoutSeconds is { } total ? TimeSpan.FromSeconds(total) : null,
    };
}

/// <summary>Writes a limit nearing, and a limit reached, to the log.</summary>
public sealed class LoggedAiNotices(ILogger<LoggedAiNotices> logger) : IAiNotices
{
    public Task LimitNearingAsync(AiLimitNotice notice, CancellationToken cancellationToken)
    {
        logger.LogWarning("AI limit {LimitId} ({Kind}, {Period}) on {Scope} is at {Spent} of {Amount}; it frees at {FreesAt:u}.", notice.Limit.LimitId,
            notice.Limit.Kind, notice.Limit.Period, notice.Limit.ScopeId?.ToString() ?? "the site", notice.Limit.Spent, notice.Limit.Amount, notice.Limit.FreesAt);
        return Task.CompletedTask;
    }

    public Task LimitReachedAsync(AiLimitNotice notice, CancellationToken cancellationToken)
    {
        logger.LogWarning("AI limit {LimitId} ({Kind}, {Period}) on {Scope} is reached ({Amount}); it frees at {FreesAt:u}.", notice.Limit.LimitId,
            notice.Limit.Kind, notice.Limit.Period, notice.Limit.ScopeId?.ToString() ?? "the site", notice.Limit.Amount, notice.Limit.FreesAt);
        return Task.CompletedTask;
    }
}

/// <summary>Writes the gateway's audited actions to the log, for a host with no audit log of its own.</summary>
public sealed class LoggedAiAudit(ILogger<LoggedAiAudit> logger) : IAiAudit
{
    public Task RecordAsync(AiAuditEntry entry, CancellationToken cancellationToken)
    {
        logger.LogInformation("AI audit: {Action} by {Actor} on {Target} ({Details}).", entry.Action, entry.Actor.Id, entry.TargetId,
            string.Join(", ", entry.Details.Select(d => $"{d.Key}={d.Value}")));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Lets go reservations no one settled (a server stopped mid-call), deletes kept prompts past the site's period (all of
/// them once the site stops keeping prompts), and deletes exports of calls past their days.
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
            DateTime now = DateTime.UtcNow;
            await db.AiExports.Where(e => e.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        }, requiresNew: true, cancellationToken: ct);
    }
}
