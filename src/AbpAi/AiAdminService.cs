using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Security.Encryption;

namespace AbpAi;

/// <summary>The site's settings as an admin reads and sets them.</summary>
/// <param name="ScopeDataClasses">The classes a scope's own connections may list at most: a scope's admin can't widen what leaves.</param>
public sealed record AiSiteSettings(bool ScopesMayConnect, bool KeepPrompts, int PromptDays, IReadOnlyList<string> ScopeDataClasses, IReadOnlyList<string> DataClasses);

/// <summary>A connection as an admin sets it. A null key keeps the one there; an empty one takes it away.</summary>
public sealed record AiConnectionInput(string Name, string Provider, IReadOnlyList<string> Models, IReadOnlyList<string> DataClasses)
{
    public string? Endpoint { get; init; }
    public string? ApiKey { get; init; }
    public IReadOnlyList<string>? Labels { get; init; }
    public IReadOnlyDictionary<string, string>? Settings { get; init; }
    public decimal InputPrice { get; init; }
    public decimal OutputPrice { get; init; }
    public IReadOnlyDictionary<string, AiPrice>? ModelPrices { get; init; }
    public bool StructuredOutput { get; init; }
    public bool Enabled { get; init; } = true;
}

/// <summary>A connection as an admin reads it: whether it has a key, never the key.</summary>
public sealed record AiConnectionInfo(Guid Id, Guid? ScopeId, string Name, string Provider, string Endpoint, bool HasKey, IReadOnlyList<string> Models,
    IReadOnlyList<string> DataClasses, IReadOnlyList<string> Labels, IReadOnlyDictionary<string, string> Settings, decimal InputPrice, decimal OutputPrice,
    IReadOnlyDictionary<string, AiPrice> ModelPrices, bool StructuredOutput, bool Enabled);

public sealed record AiAliasInfo(Guid Id, Guid? ScopeId, string Name, IReadOnlyList<AiAliasTarget> Targets, DateTime UpdatedAt);

/// <summary>A limit as an admin sets it: its kind (<see cref="AiLimitKinds"/>), period, amount, and whose for <c>account</c> or <c>group</c>.</summary>
public sealed record AiLimitInput(string Kind, string Period, decimal Amount, Guid? AccountId = null, Guid? GroupId = null);

public sealed record AiLimitInfo(Guid Id, Guid? ScopeId, string Kind, string Period, decimal Amount, Guid? AccountId, Guid? GroupId);

/// <summary>
/// The admin API: the site's settings, and connections, aliases and limits on the site or a scope. The site's admins
/// set anything; a scope's admins set their scope's and those of the scopes under it, connections only while the site
/// lets scopes connect, and only with the data classes the site lets scopes' connections receive.
/// </summary>
public sealed class AiAdminService(AiDb aiDb, IAiRights rights, AiScopeChains chains, AiDataRules rules, IEnumerable<IAiProvider> providers,
    IStringEncryptionService encryption) : ITransientDependency
{
    /// <summary>The classes a scope's connections may list when the site hasn't said: nothing of the organization's data.</summary>
    public static readonly IReadOnlyList<string> DefaultScopeDataClasses = [AiDataClasses.Public, AiDataClasses.Internal];

    public async Task<AiSiteSettings> GetSiteAsync(AiActor actor, CancellationToken ct)
    {
        await SiteAdminAsync(actor, ct);
        AiSite site = await aiDb.RunAsync(db => SiteAsync(db, ct), cancellationToken: ct);
        return Info(site);
    }

    public async Task<AiSiteSettings> SetSiteAsync(AiActor actor, AiSiteSettings input, CancellationToken ct)
    {
        await SiteAdminAsync(actor, ct);
        if (input.PromptDays is < 1 or > 3650)
        {
            throw new UserFriendlyException("Prompts are kept from 1 to 3650 days.", AiErrorCodes.Invalid);
        }

        List<string> scopeClasses = rules.Checked(input.ScopeDataClasses);
        return await aiDb.RunAsync(async db =>
        {
            AiSite site = await SiteAsync(db, ct);
            site.ScopesMayConnect = input.ScopesMayConnect;
            site.KeepPrompts = input.KeepPrompts;
            site.PromptDays = input.PromptDays;
            site.ScopeDataClasses = scopeClasses;
            site.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Info(site);
        }, cancellationToken: ct);
    }

    public async Task<List<AiConnectionInfo>> ListConnectionsAsync(AiActor actor, Guid? scopeId, CancellationToken ct)
    {
        await AdminAsync(actor, scopeId, ct);
        List<AiConnection> found = await aiDb.RunAsync(db => db.AiConnections.AsNoTracking().Where(c => c.ScopeId == scopeId).OrderBy(c => c.Name).ToListAsync(ct),
            cancellationToken: ct);
        return [.. found.Select(Info)];
    }

    public async Task<AiConnectionInfo> CreateConnectionAsync(AiActor actor, Guid? scopeId, AiConnectionInput input, CancellationToken ct)
    {
        await AdminAsync(actor, scopeId, ct);
        return await aiDb.RunAsync(async db =>
        {
            await ScopeMayConnectAsync(db, scopeId, input, ct);
            if (await db.AiConnections.AnyAsync(c => c.ScopeId == scopeId && c.Name == input.Name.Trim(), ct))
            {
                throw new UserFriendlyException($"There's a connection \"{input.Name}\" here already.", AiErrorCodes.Invalid);
            }

            AiConnection connection = new(Guid.NewGuid(), scopeId, input.Name.Trim(), input.Provider.Trim().ToLowerInvariant());
            Apply(connection, input);
            db.AiConnections.Add(connection);
            await db.SaveChangesAsync(ct);
            return Info(connection);
        }, cancellationToken: ct);
    }

    public async Task<AiConnectionInfo> UpdateConnectionAsync(AiActor actor, Guid id, AiConnectionInput input, CancellationToken ct)
    {
        AiConnection existing = await aiDb.RunAsync(db => db.AiConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct), cancellationToken: ct)
            ?? throw new EntityNotFoundException(typeof(AiConnection), id);
        await AdminAsync(actor, existing.ScopeId, ct);
        return await aiDb.RunAsync(async db =>
        {
            await ScopeMayConnectAsync(db, existing.ScopeId, input, ct);
            AiConnection connection = await db.AiConnections.FirstAsync(c => c.Id == id, ct);
            connection.Name = input.Name.Trim();
            connection.Provider = input.Provider.Trim().ToLowerInvariant();
            Apply(connection, input);
            await db.SaveChangesAsync(ct);
            return Info(connection);
        }, cancellationToken: ct);
    }

    public async Task DeleteConnectionAsync(AiActor actor, Guid id, CancellationToken ct)
    {
        AiConnection existing = await aiDb.RunAsync(db => db.AiConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct), cancellationToken: ct)
            ?? throw new EntityNotFoundException(typeof(AiConnection), id);
        await AdminAsync(actor, existing.ScopeId, ct);
        await aiDb.RunAsync(db => db.AiConnections.Where(c => c.Id == id).ExecuteDeleteAsync(ct), cancellationToken: ct);
    }

    public async Task<List<AiAliasInfo>> ListAliasesAsync(AiActor actor, Guid? scopeId, CancellationToken ct)
    {
        await AdminAsync(actor, scopeId, ct);
        List<AiAlias> found = await aiDb.RunAsync(db => db.AiAliases.AsNoTracking().Where(a => a.ScopeId == scopeId).OrderBy(a => a.Name).ToListAsync(ct),
            cancellationToken: ct);
        return [.. found.Select(Info)];
    }

    /// <summary>Sets an alias on the site or a scope: its targets in order, each a connection the scope sees.</summary>
    public async Task<AiAliasInfo> SetAliasAsync(AiActor actor, Guid? scopeId, string name, IReadOnlyList<AiAliasTarget> targets, CancellationToken ct)
    {
        await AdminAsync(actor, scopeId, ct);
        string key = name.Trim();
        if (key.Length is 0 or > 100 || targets.Count == 0)
        {
            throw new UserFriendlyException("An alias has a name and at least one target.", AiErrorCodes.Invalid);
        }

        AiScopeChain chain = await chains.OfAsync(scopeId, ct);
        return await aiDb.RunAsync(async db =>
        {
            List<Guid> ids = [.. targets.Select(t => t.ConnectionId).Distinct()];
            List<AiConnection> connections = await db.AiConnections.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct);
            foreach (AiAliasTarget target in targets)
            {
                AiConnection? connection = connections.FirstOrDefault(c => c.Id == target.ConnectionId);
                if (connection is null || !chain.Sees(connection.ScopeId))
                {
                    throw new UserFriendlyException($"Connection {target.ConnectionId} isn't one this scope can use.", AiErrorCodes.Invalid);
                }

                if (target.Model is not null && connection.Models.Count > 0 && !connection.Models.Contains(target.Model))
                {
                    throw new UserFriendlyException($"{connection.Name} doesn't offer {target.Model}.", AiErrorCodes.Invalid);
                }
            }

            AiAlias? alias = await db.AiAliases.FirstOrDefaultAsync(a => a.ScopeId == scopeId && a.Name == key, ct);
            if (alias is null)
            {
                alias = new AiAlias(Guid.NewGuid(), scopeId, key);
                db.AiAliases.Add(alias);
            }

            alias.Targets = [.. targets];
            alias.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Info(alias);
        }, cancellationToken: ct);
    }

    public async Task DeleteAliasAsync(AiActor actor, Guid? scopeId, string name, CancellationToken ct)
    {
        await AdminAsync(actor, scopeId, ct);
        string key = name.Trim();
        await aiDb.RunAsync(db => db.AiAliases.Where(a => a.ScopeId == scopeId && a.Name == key).ExecuteDeleteAsync(ct), cancellationToken: ct);
    }

    public async Task<List<AiLimitInfo>> ListLimitsAsync(AiActor actor, Guid? scopeId, CancellationToken ct)
    {
        await AdminAsync(actor, scopeId, ct);
        List<AiLimit> found = await aiDb.RunAsync(db => db.AiLimits.AsNoTracking().Where(l => l.ScopeId == scopeId).OrderBy(l => l.Kind).ThenBy(l => l.Period)
            .ToListAsync(ct), cancellationToken: ct);
        return [.. found.Select(Info)];
    }

    /// <summary>Sets a limit on the site or a scope; one of the same kind, period and whose is changed in place.</summary>
    public async Task<AiLimitInfo> SetLimitAsync(AiActor actor, Guid? scopeId, AiLimitInput input, CancellationToken ct)
    {
        await AdminAsync(actor, scopeId, ct);
        if (!AiLimitKinds.All.Contains(input.Kind) || !AiPeriods.All.Contains(input.Period) || input.Amount < 0
            || (input.Kind == AiLimitKinds.Account) != (input.AccountId is not null) || (input.Kind == AiLimitKinds.Group) != (input.GroupId is not null))
        {
            throw new UserFriendlyException("A limit is total, person, account (with an account) or group (with a group); over a day, week or month; and not below nothing.",
                AiErrorCodes.Invalid);
        }

        return await aiDb.RunAsync(async db =>
        {
            AiLimit? limit = await db.AiLimits.FirstOrDefaultAsync(l => l.ScopeId == scopeId && l.Kind == input.Kind && l.Period == input.Period
                && l.AccountId == input.AccountId && l.GroupId == input.GroupId, ct);
            if (limit is null)
            {
                limit = new AiLimit(Guid.NewGuid(), scopeId, input.Kind, input.Period, input.Amount, input.AccountId, input.GroupId);
                db.AiLimits.Add(limit);
            }

            limit.Amount = input.Amount;
            await db.SaveChangesAsync(ct);
            return Info(limit);
        }, cancellationToken: ct);
    }

    public async Task DeleteLimitAsync(AiActor actor, Guid id, CancellationToken ct)
    {
        AiLimit limit = await aiDb.RunAsync(db => db.AiLimits.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct), cancellationToken: ct)
            ?? throw new EntityNotFoundException(typeof(AiLimit), id);
        await AdminAsync(actor, limit.ScopeId, ct);
        await aiDb.RunAsync(db => db.AiLimits.Where(l => l.Id == id).ExecuteDeleteAsync(ct), cancellationToken: ct);
    }

    /// <summary>The actor administers the site, or the scope or one above it.</summary>
    public async Task<bool> AdministersAsync(AiActor actor, Guid? scopeId, CancellationToken ct)
    {
        if (await rights.IsSiteAdminAsync(actor, ct))
        {
            return true;
        }

        if (scopeId is null)
        {
            return false;
        }

        foreach (Guid scope in (await chains.OfAsync(scopeId, ct)).Up)
        {
            if (await rights.AdministersScopeAsync(actor, scope, ct))
            {
                return true;
            }
        }

        return false;
    }

    private async Task AdminAsync(AiActor actor, Guid? scopeId, CancellationToken ct)
    {
        if (!await AdministersAsync(actor, scopeId, ct))
        {
            throw new UserFriendlyException(scopeId is null ? "Only the site's admins set the site's AI." : "Only this scope's admins set its AI.", AiErrorCodes.Forbidden);
        }
    }

    private async Task SiteAdminAsync(AiActor actor, CancellationToken ct)
    {
        if (!await rights.IsSiteAdminAsync(actor, ct))
        {
            throw new UserFriendlyException("Only the site's admins set the site's AI.", AiErrorCodes.Forbidden);
        }
    }

    private async Task ScopeMayConnectAsync(IAiDbContext db, Guid? scopeId, AiConnectionInput input, CancellationToken ct)
    {
        if (!providers.Any(p => string.Equals(p.Kind, input.Provider.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new UserFriendlyException($"There's no provider \"{input.Provider}\" ({string.Join(", ", providers.Select(p => p.Kind).Distinct())}).", AiErrorCodes.Invalid);
        }

        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 100 || input.Models.Count == 0)
        {
            throw new UserFriendlyException("A connection has a name and at least one model.", AiErrorCodes.Invalid);
        }

        List<string> classes = rules.Checked(input.DataClasses);
        if (scopeId is null)
        {
            return;
        }

        AiSite site = await SiteAsync(db, ct);
        if (!site.ScopesMayConnect)
        {
            throw new UserFriendlyException("The site doesn't let scopes set connections of their own.", AiErrorCodes.Forbidden);
        }

        List<string> beyond = [.. classes.Where(c => !site.ScopeDataClasses.Contains(c, StringComparer.OrdinalIgnoreCase))];
        if (beyond.Count > 0)
        {
            throw new UserFriendlyException($"A scope's connection may not receive {string.Join(", ", beyond)} data: the site lets it receive only "
                + $"{string.Join(", ", site.ScopeDataClasses)}.", AiErrorCodes.Forbidden);
        }
    }

    private void Apply(AiConnection connection, AiConnectionInput input)
    {
        connection.Endpoint = input.Endpoint?.Trim() ?? "";
        if (input.ApiKey is not null)
        {
            connection.ProtectedKey = input.ApiKey.Length == 0 ? null : encryption.Encrypt(input.ApiKey);
        }

        connection.Models = [.. input.Models.Select(m => m.Trim()).Where(m => m.Length > 0).Distinct()];
        connection.DataClasses = rules.Checked(input.DataClasses);
        connection.Labels = [.. (input.Labels ?? []).Select(l => l.Trim()).Where(l => l.Length > 0).Distinct()];
        connection.Settings = input.Settings?.ToDictionary() ?? [];
        connection.InputPrice = input.InputPrice;
        connection.OutputPrice = input.OutputPrice;
        connection.ModelPrices = input.ModelPrices?.ToDictionary() ?? [];
        connection.StructuredOutput = input.StructuredOutput;
        connection.Enabled = input.Enabled;
    }

    private static async Task<AiSite> SiteAsync(IAiDbContext db, CancellationToken ct)
    {
        AiSite? site = await db.AiSites.FirstOrDefaultAsync(s => s.Id == AiSite.TheId, ct);
        if (site is null)
        {
            site = new AiSite(AiSite.TheId) { ScopeDataClasses = [.. DefaultScopeDataClasses] };
            db.AiSites.Add(site);
        }

        return site;
    }

    private AiSiteSettings Info(AiSite site) => new(site.ScopesMayConnect, site.KeepPrompts, site.PromptDays, site.ScopeDataClasses, rules.Classes);

    private static AiConnectionInfo Info(AiConnection c) => new(c.Id, c.ScopeId, c.Name, c.Provider, c.Endpoint, c.ProtectedKey is not null, c.Models, c.DataClasses,
        c.Labels, c.Settings, c.InputPrice, c.OutputPrice, c.ModelPrices, c.StructuredOutput, c.Enabled);

    private static AiAliasInfo Info(AiAlias a) => new(a.Id, a.ScopeId, a.Name, a.Targets, a.UpdatedAt);

    private static AiLimitInfo Info(AiLimit l) => new(l.Id, l.ScopeId, l.Kind, l.Period, l.Amount, l.AccountId, l.GroupId);
}
