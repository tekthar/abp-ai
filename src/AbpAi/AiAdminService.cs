using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Entities;
using Microsoft.Extensions.Options;
using Volo.Abp.Security.Encryption;

namespace AbpAi;

/// <summary>The site's settings as an admin reads and sets them.</summary>
/// <param name="ScopeDataClasses">The classes a scope's own connections may list at most: a scope's admin can't widen what leaves.</param>
public sealed record AiSiteSettings(bool ScopesMayConnect, bool KeepPrompts, int PromptDays, IReadOnlyList<string> ScopeDataClasses, IReadOnlyList<string> DataClasses)
{
    /// <summary>
    /// How scopes use the site's models: <c>open</c> (every scope, every model) or <c>listed</c> (as each scope's access
    /// says). Always set when read; null when set keeps what's there.
    /// </summary>
    public string? ScopeAccess { get; init; }
}

/// <summary>Which of the site's models a scope may use, when the site lists them, and whether it may connect its own.</summary>
/// <param name="SiteModels">The site's models it may use (a model by its id or its name), when not all of them.</param>
public sealed record AiScopeAccessInput(bool AllSiteModels, IReadOnlyList<AiModelRef>? SiteModels, bool MayConnect);

/// <summary>
/// The access that applies in a scope: set on it, or on the nearest scope above it (<see cref="SetOnScopeId"/>); with
/// none set anywhere above it (<see cref="SetOnScopeId"/> null) it may use none of the site's models, nor connect its own.
/// </summary>
public sealed record AiScopeAccessInfo(Guid ScopeId, Guid? SetOnScopeId, bool AllSiteModels, IReadOnlyList<AiModelRef> SiteModels, bool MayConnect);

/// <summary>
/// A connection as an admin sets it. A null key keeps the one there; an empty one takes it away. Null model settings keep
/// the ones there (for the models it still offers).
/// </summary>
public sealed record AiConnectionInput(string Name, string Provider, IReadOnlyList<string> Models, IReadOnlyList<string> DataClasses)
{
    /// <summary>Its models' settings, by their ids; a model left out has none (it's on and takes everything).</summary>
    public IReadOnlyDictionary<string, AiModelSettings>? ModelSettings { get; init; }

    public string? Endpoint { get; init; }
    public string? ApiKey { get; init; }
    public IReadOnlyList<string>? Labels { get; init; }
    public IReadOnlyDictionary<string, string>? Settings { get; init; }
    public decimal InputPrice { get; init; }
    public decimal OutputPrice { get; init; }
    public IReadOnlyDictionary<string, AiPrice>? ModelPrices { get; init; }
    public bool StructuredOutput { get; init; }
    public bool Enabled { get; init; } = true;

    /// <summary>Per million input tokens written to the provider's cache; null: 1.25 times the input price.</summary>
    public decimal? CacheWritePrice { get; init; }

    /// <summary>Per million input tokens read from the provider's cache; null: a tenth of the input price.</summary>
    public decimal? CacheReadPrice { get; init; }

    /// <summary>
    /// The longest wait, in seconds (1 to 3600), for the next part of a reply: a streamed call's first event and each one
    /// after it. Null keeps what's set (a new connection has the site's); 0 goes back to the site's.
    /// </summary>
    public int? IdleTimeoutSeconds { get; init; }

    /// <summary>The longest, in seconds (1 to 86400), a whole call may take. Null keeps what's set; 0 goes back to the site's.</summary>
    public int? TotalTimeoutSeconds { get; init; }

    /// <summary>Test the connection once it's saved (<see cref="AiAdminService.TestConnectionAsync"/>); how it went is in <see cref="AiConnectionInfo.Test"/>.</summary>
    public bool Test { get; init; }
}

/// <summary>
/// How a connection's test went: one small call to a model, answered (with the time it took) or not, with the provider's
/// status and its own reason ("invalid x-api-key", "model not found", "it couldn't be reached"). Never the key.
/// </summary>
public sealed record AiConnectionTest(bool Ok, string Model, int ElapsedMs, DateTime At)
{
    /// <summary>The provider's answer's status, when it answered with an error.</summary>
    public int? Status { get; init; }

    /// <summary>Why it failed, in the provider's own words where it gave them.</summary>
    public string? Reason { get; init; }
}

/// <summary>A connection as an admin reads it: whether it has a key, never the key.</summary>
public sealed record AiConnectionInfo(Guid Id, Guid? ScopeId, string Name, string Provider, string Endpoint, bool HasKey, IReadOnlyList<string> Models,
    IReadOnlyList<string> DataClasses, IReadOnlyList<string> Labels, IReadOnlyDictionary<string, string> Settings, decimal InputPrice, decimal OutputPrice,
    IReadOnlyDictionary<string, AiPrice> ModelPrices, bool StructuredOutput, bool Enabled)
{
    /// <summary>Its models' settings, by their ids.</summary>
    public IReadOnlyDictionary<string, AiModelSettings> ModelSettings { get; init; } = new Dictionary<string, AiModelSettings>();

    public decimal? CacheWritePrice { get; init; }

    public decimal? CacheReadPrice { get; init; }

    /// <summary>Its own idle timeout, in seconds; null: the site's.</summary>
    public int? IdleTimeoutSeconds { get; init; }

    /// <summary>Its own total timeout, in seconds; null: the site's.</summary>
    public int? TotalTimeoutSeconds { get; init; }

    /// <summary>The host's own name for its type, when it keeps the connection and names one.</summary>
    public string? Type { get; init; }

    /// <summary>How its test went, when it was saved with <see cref="AiConnectionInput.Test"/>.</summary>
    public AiConnectionTest? Test { get; init; }
}

public sealed record AiAliasInfo(Guid Id, Guid? ScopeId, string Name, IReadOnlyList<AiAliasTarget> Targets, DateTime UpdatedAt);

/// <summary>
/// A limit as an admin sets it: its kind (<see cref="AiLimitKinds"/>), period (none for <c>call-tokens</c>), amount, whose
/// for <c>account</c> or <c>group</c> (and, for <c>call-tokens</c>, an account when it's one account's), and what it counts.
/// </summary>
public sealed record AiLimitInput(string Kind, string Period, decimal Amount, Guid? AccountId = null, Guid? GroupId = null)
{
    /// <summary>What it counts: <c>cost</c>, <c>tokens</c> or <c>requests</c> (<see cref="AiLimitMeasures"/>); <c>call-tokens</c> counts tokens.</summary>
    public string Measure { get; init; } = AiLimitMeasures.Cost;
}

public sealed record AiLimitInfo(Guid Id, Guid? ScopeId, string Kind, string Period, decimal Amount, Guid? AccountId, Guid? GroupId)
{
    public string Measure { get; init; } = AiLimitMeasures.Cost;
}

/// <summary>
/// The admin API: the site's settings, and connections, aliases and limits on the site or a scope. The site's admins
/// set anything; a scope's admins set their scope's and those of the scopes under it, connections only while the site
/// lets scopes connect, and only with the data classes the site lets scopes' connections receive. When the host keeps
/// the connections (its own <see cref="IAiConnectionSource"/>), connections are listed from it and set where it keeps
/// them: creating, changing or deleting one here is refused with <see cref="AiErrorCodes.HostConnections"/>.
/// </summary>
public sealed class AiAdminService(AiDb aiDb, IAiRights rights, AiScopeChains chains, AiDataRules rules, IEnumerable<IAiProvider> providers,
    IStringEncryptionService encryption, IAiConnectionSource source, IOptions<AiOptions> options) : ITransientDependency
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
        if (input.ScopeAccess is not null && !AiScopeAccessModes.All.Contains(input.ScopeAccess))
        {
            throw new UserFriendlyException($"Scopes' access is {string.Join(" or ", AiScopeAccessModes.All)}.", AiErrorCodes.Invalid);
        }

        return await aiDb.RunAsync(async db =>
        {
            AiSite site = await SiteAsync(db, ct);
            site.ScopeAccess = input.ScopeAccess ?? site.ScopeAccess;
            site.ScopesMayConnect = input.ScopesMayConnect;
            site.KeepPrompts = input.KeepPrompts;
            site.PromptDays = input.PromptDays;
            site.ScopeDataClasses = scopeClasses;
            site.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Info(site);
        }, cancellationToken: ct);
    }

    /// <summary>Whether AbpAi keeps the connections itself (its own table), so they're set here.</summary>
    public bool KeepsConnections => source is StoredAiConnections;

    /// <summary>The connections set on the site or a scope, from wherever they're kept.</summary>
    /// <summary>The access that applies in a scope, when the site lists what each scope may use: its own, or the nearest above it.</summary>
    public async Task<AiScopeAccessInfo> GetScopeAccessAsync(AiActor actor, Guid scopeId, CancellationToken ct)
    {
        await SiteAdminAsync(actor, ct);
        AiScopeChain chain = await chains.OfAsync(scopeId, ct);
        AiScopeAccess? access = await aiDb.RunAsync(db => AiRouter.AccessOfAsync(db, chain, ct), cancellationToken: ct);
        return Info(scopeId, access);
    }

    /// <summary>Every scope's access that's set.</summary>
    public async Task<List<AiScopeAccessInfo>> ListScopeAccessAsync(AiActor actor, CancellationToken ct)
    {
        await SiteAdminAsync(actor, ct);
        List<AiScopeAccess> found = await aiDb.RunAsync(db => db.AiScopeAccess.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct), cancellationToken: ct);
        return [.. found.Select(a => Info(a.Id, a))];
    }

    /// <summary>
    /// Sets which of the site's models a scope (and the scopes under it with none of their own) may use, and whether it may
    /// connect its own: for when the site lists them. Each model is one of the site's connections'.
    /// </summary>
    public async Task<AiScopeAccessInfo> SetScopeAccessAsync(AiActor actor, Guid scopeId, AiScopeAccessInput input, CancellationToken ct)
    {
        await SiteAdminAsync(actor, ct);
        IReadOnlyList<AiConnectionDescriptor> site = await source.ListAsync([], ct);
        List<AiModelRef> models = [];
        foreach (AiModelRef wanted in input.SiteModels ?? [])
        {
            AiConnectionDescriptor connection = site.FirstOrDefault(c => c.Id == wanted.ConnectionId && c.ScopeId is null)
                ?? throw new UserFriendlyException($"Connection {wanted.ConnectionId} isn't one of the site's.", AiErrorCodes.Invalid);
            AiModelRef model = new(connection.Id, connection.ModelIdOf(wanted.Model?.Trim() ?? "")
                ?? throw new UserFriendlyException($"{connection.Name} doesn't offer {wanted.Model}.", AiErrorCodes.Invalid));
            if (!models.Contains(model))
            {
                models.Add(model);
            }
        }

        return await aiDb.RunAsync(async db =>
        {
            AiScopeAccess? access = await db.AiScopeAccess.FirstOrDefaultAsync(a => a.Id == scopeId, ct);
            if (access is null)
            {
                access = new AiScopeAccess(scopeId);
                db.AiScopeAccess.Add(access);
            }

            access.AllSiteModels = input.AllSiteModels;
            access.SiteModels = models;
            access.MayConnect = input.MayConnect;
            access.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Info(scopeId, access);
        }, cancellationToken: ct);
    }

    /// <summary>Takes a scope's own access away: the nearest above it applies again.</summary>
    public async Task DeleteScopeAccessAsync(AiActor actor, Guid scopeId, CancellationToken ct)
    {
        await SiteAdminAsync(actor, ct);
        await aiDb.RunAsync(db => db.AiScopeAccess.Where(a => a.Id == scopeId).ExecuteDeleteAsync(ct), cancellationToken: ct);
    }

    public async Task<List<AiConnectionInfo>> ListConnectionsAsync(AiActor actor, Guid? scopeId, CancellationToken ct)
    {
        await AdminAsync(actor, scopeId, ct);
        IReadOnlyList<AiConnectionDescriptor> found = await source.ListAsync(scopeId is { } scope ? [scope] : [], ct);
        return [.. found.Where(c => c.ScopeId == scopeId).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(Info)];
    }

    public async Task<AiConnectionInfo> CreateConnectionAsync(AiActor actor, Guid? scopeId, AiConnectionInput input, CancellationToken ct)
    {
        KeptHere();
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
            return connection;
        }, cancellationToken: ct).ContinueWithTestAsync(this, input, ct);
    }

    public async Task<AiConnectionInfo> UpdateConnectionAsync(AiActor actor, Guid id, AiConnectionInput input, CancellationToken ct)
    {
        KeptHere();
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
            return connection;
        }, cancellationToken: ct).ContinueWithTestAsync(this, input, ct);
    }

    /// <summary>
    /// Tests a connection: one small call (a few tokens) to the model named (by its id or name), else its first that's
    /// on, straight to its provider, within its idle timeout and with no second try. It says whether it answered and how
    /// long it took, or the provider's status and its own reason. Nothing is kept on the ledger. From wherever
    /// connections are kept, so a host that keeps its own tests them too.
    /// </summary>
    public async Task<AiConnectionTest> TestConnectionAsync(AiActor actor, Guid? scopeId, Guid connectionId, string? model, CancellationToken ct)
    {
        await AdminAsync(actor, scopeId, ct);
        AiConnectionDescriptor connection = (await source.ListAsync(scopeId is { } scope ? [scope] : [], ct)).FirstOrDefault(c => c.Id == connectionId && c.ScopeId == scopeId)
            ?? throw new EntityNotFoundException(typeof(AiConnection), connectionId);
        return await TestAsync(connection, model, ct);
    }

    /// <summary>A saved connection as an admin reads it, with its test when the input asked for one.</summary>
    internal async Task<AiConnectionInfo> SavedAsync(AiConnection connection, AiConnectionInput input, CancellationToken ct) =>
        input.Test ? Info(connection) with { Test = await TestAsync(StoredAiConnections.Describe(connection), null, ct) } : Info(connection);

    internal async Task<AiConnectionTest> TestAsync(AiConnectionDescriptor connection, string? model, CancellationToken ct)
    {
        DateTime at = DateTime.UtcNow;
        string chosen = model is { Length: > 0 } ? connection.ModelIdOf(model) ?? model : AiRouter.DefaultModel(connection) ?? connection.Models.FirstOrDefault() ?? "";
        IAiProvider? provider = providers.LastOrDefault(p => string.Equals(p.Kind, connection.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            return new AiConnectionTest(false, chosen, 0, at) { Reason = $"No provider \"{connection.Provider}\" is installed." };
        }

        if (chosen.Length == 0)
        {
            return new AiConnectionTest(false, chosen, 0, at) { Reason = "It lists no models to call." };
        }

        AiTimeoutsOf timeouts = AiTimeoutsOf.For(connection, options.Value);
        TimeSpan wait = timeouts.Idle < timeouts.Total ? timeouts.Idle : timeouts.Total;
        Stopwatch watch = Stopwatch.StartNew();
        string? key = null;
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(wait);
        try
        {
            key = await source.KeyAsync(connection.Id, ct);
            AiTarget target = new(connection.Id, connection.Name, connection.Endpoint, key, chosen, connection.Settings);
            await provider.CompleteAsync(new AiProviderCall(target, AiMessages.User("Reply with OK."), null, null, 16, null)
            {
                Purpose = "connection-test",
                Retries = 0,
            }, limit.Token);
            return new AiConnectionTest(true, chosen, (int)watch.ElapsedMilliseconds, at);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            int elapsed = (int)watch.ElapsedMilliseconds;
            return ex switch
            {
                OperationCanceledException => new AiConnectionTest(false, chosen, elapsed, at) { Reason = $"It didn't answer in {AiTimeoutsOf.Words(wait)}." },
                AiProviderException failed => new AiConnectionTest(false, chosen, elapsed, at)
                {
                    Status = failed.Status,
                    Reason = failed.Reason ?? Providers.ProviderHttp.Masked(failed.Message, key),
                },
                HttpRequestException unreached => new AiConnectionTest(false, chosen, elapsed, at)
                {
                    Status = unreached.StatusCode is { } code ? (int)code : null,
                    Reason = $"It couldn't be reached: {Providers.ProviderHttp.Masked(unreached.InnerException?.Message ?? unreached.Message, key)}",
                },
                _ => new AiConnectionTest(false, chosen, elapsed, at) { Reason = Providers.ProviderHttp.Masked(ex.Message, key) },
            };
        }
    }

    public async Task DeleteConnectionAsync(AiActor actor, Guid id, CancellationToken ct)
    {
        KeptHere();
        AiConnection existing = await aiDb.RunAsync(db => db.AiConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct), cancellationToken: ct)
            ?? throw new EntityNotFoundException(typeof(AiConnection), id);
        await AdminAsync(actor, existing.ScopeId, ct);
        await aiDb.RunAsync(db => db.AiConnections.Where(c => c.Id == id).ExecuteDeleteAsync(ct), cancellationToken: ct);
    }

    /// <summary>
    /// The models a connection's provider offers, by their ids, asked of the provider through the connection: from
    /// wherever connections are kept, so a host that keeps its own can find its models with the gateway's providers.
    /// </summary>
    public async Task<IReadOnlyList<string>> ProviderModelsAsync(AiActor actor, Guid? scopeId, Guid connectionId, CancellationToken ct)
    {
        await AdminAsync(actor, scopeId, ct);
        AiConnectionDescriptor connection = (await source.ListAsync(scopeId is { } scope ? [scope] : [], ct)).FirstOrDefault(c => c.Id == connectionId && c.ScopeId == scopeId)
            ?? throw new EntityNotFoundException(typeof(AiConnection), connectionId);
        return await OfferedAsync(connection, ct);
    }

    /// <summary>
    /// Asks a connection's provider which models it offers and adds the ones it doesn't list yet, each off (its settings'
    /// <c>Enabled</c> false) until an admin turns it on. Only for connections AbpAi keeps.
    /// </summary>
    public async Task<AiConnectionInfo> DiscoverModelsAsync(AiActor actor, Guid connectionId, CancellationToken ct)
    {
        KeptHere();
        AiConnection existing = await aiDb.RunAsync(db => db.AiConnections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == connectionId, ct), cancellationToken: ct)
            ?? throw new EntityNotFoundException(typeof(AiConnection), connectionId);
        await AdminAsync(actor, existing.ScopeId, ct);
        IReadOnlyList<string> offered = await OfferedAsync(StoredAiConnections.Describe(existing), ct);
        return await aiDb.RunAsync(async db =>
        {
            AiConnection connection = await db.AiConnections.FirstAsync(c => c.Id == connectionId, ct);
            List<string> found = [.. offered.Select(m => m.Trim()).Where(m => m.Length is > 0 and <= 200 && !connection.Models.Contains(m)).Distinct()];
            Dictionary<string, AiModelSettings> settings = new(connection.ModelSettings);
            foreach (string model in found)
            {
                // An embedding model says so in its id (text-embedding-3-small, nomic-embed-text, voyage's embed models).
                settings[model] = new AiModelSettings { Enabled = false, Embeds = model.Contains("embed", StringComparison.OrdinalIgnoreCase) };
            }

            connection.Models = [.. connection.Models, .. found];
            connection.ModelSettings = settings;
            await db.SaveChangesAsync(ct);
            return Info(connection);
        }, cancellationToken: ct);
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
        IReadOnlyList<AiConnectionDescriptor> connections = await source.ListAsync([.. chain.Up], ct);
        AiScopeAccess? access = null;
        bool listed = scopeId is not null && await aiDb.RunAsync(async db =>
        {
            bool isListed = (await db.AiSites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == AiSite.TheId, ct))?.ScopeAccess == AiScopeAccessModes.Listed;
            access = isListed ? await AiRouter.AccessOfAsync(db, chain, ct) : null;
            return isListed;
        }, cancellationToken: ct);
        List<AiAliasTarget> kept = [];
        foreach (AiAliasTarget target in targets)
        {
            AiConnectionDescriptor? connection = connections.FirstOrDefault(c => c.Id == target.ConnectionId);
            if (connection is null || !chain.Sees(connection.ScopeId))
            {
                throw new UserFriendlyException($"Connection {target.ConnectionId} isn't one this scope can use.", AiErrorCodes.Invalid);
            }

            // A model named by its name is kept by its id, so renaming it later doesn't break the alias.
            string? model = target.Model is null || connection.Models.Count == 0 ? target.Model : connection.ModelIdOf(target.Model)
                ?? throw new UserFriendlyException($"{connection.Name} doesn't offer {target.Model}.", AiErrorCodes.Invalid);

            // When the site lists what scopes may use, a scope's alias leads only to the site's models open to it.
            if (listed && connection.ScopeId is null && (model ?? AiRouter.DefaultModel(connection)) is { } used && access?.Opens(connection.Id, used) != true)
            {
                throw new UserFriendlyException($"{connection.Name}'s {used} isn't open to this scope.", AiErrorCodes.Invalid);
            }

            kept.Add(target with { Model = model });
        }

        return await aiDb.RunAsync(async db =>
        {
            AiAlias? alias = await db.AiAliases.FirstOrDefaultAsync(a => a.ScopeId == scopeId && a.Name == key, ct);
            if (alias is null)
            {
                alias = new AiAlias(Guid.NewGuid(), scopeId, key);
                db.AiAliases.Add(alias);
            }

            alias.Targets = kept;
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
        List<AiLimit> found = await aiDb.RunAsync(db => db.AiLimits.AsNoTracking().Where(l => l.ScopeId == scopeId).OrderBy(l => l.Kind).ThenBy(l => l.Measure)
            .ThenBy(l => l.Period).ToListAsync(ct), cancellationToken: ct);
        return [.. found.Select(Info)];
    }

    /// <summary>
    /// Sets a limit on the site or a scope; one of the same kind, measure, period and whose is changed in place. A
    /// <c>call-tokens</c> limit has no period, counts tokens, and is a whole number of them, at least one.
    /// </summary>
    public async Task<AiLimitInfo> SetLimitAsync(AiActor actor, Guid? scopeId, AiLimitInput input, CancellationToken ct)
    {
        await AdminAsync(actor, scopeId, ct);
        return await SetCheckedLimitAsync(scopeId, input, ct);
    }

    /// <summary>
    /// An account's own limits on the site (an agent's), which count its calls everywhere: for the site's admins and the
    /// account's (<see cref="IAiRights.AdministersAccountAsync"/>).
    /// </summary>
    public async Task<List<AiLimitInfo>> ListAccountLimitsAsync(AiActor actor, Guid accountId, CancellationToken ct)
    {
        await AccountAdminAsync(actor, accountId, ct);
        List<AiLimit> found = await aiDb.RunAsync(db => db.AiLimits.AsNoTracking()
            .Where(l => l.ScopeId == null && l.AccountId == accountId && (l.Kind == AiLimitKinds.Account || l.Kind == AiLimitKinds.CallTokens))
            .OrderBy(l => l.Kind).ThenBy(l => l.Measure).ThenBy(l => l.Period).ToListAsync(ct), cancellationToken: ct);
        return [.. found.Select(Info)];
    }

    /// <summary>
    /// Sets one of an account's own limits on the site: <c>account</c> (over a period) or <c>call-tokens</c>, for the
    /// account named whatever the input says. One of the same kind, measure and period is changed in place.
    /// </summary>
    public async Task<AiLimitInfo> SetAccountLimitAsync(AiActor actor, Guid accountId, AiLimitInput input, CancellationToken ct)
    {
        await AccountAdminAsync(actor, accountId, ct);
        if (input.Kind is not (AiLimitKinds.Account or AiLimitKinds.CallTokens))
        {
            throw new UserFriendlyException("An account's own limit is account (over a period) or call-tokens.", AiErrorCodes.Invalid);
        }

        return await SetCheckedLimitAsync(null, input with { AccountId = accountId, GroupId = null }, ct);
    }

    private async Task<AiLimitInfo> SetCheckedLimitAsync(Guid? scopeId, AiLimitInput input, CancellationToken ct)
    {
        bool callTokens = input.Kind == AiLimitKinds.CallTokens;
        string measure = callTokens ? AiLimitMeasures.Tokens : input.Measure ?? AiLimitMeasures.Cost;
        string period = callTokens ? "" : input.Period ?? "";
        bool shaped = AiLimitKinds.All.Contains(input.Kind) && input.Amount >= 0 && (input.Kind == AiLimitKinds.Group) == (input.GroupId is not null)
            && (callTokens
                ? string.IsNullOrEmpty(input.Period) && input.Amount >= 1 && input.Amount == decimal.Truncate(input.Amount)
                : AiPeriods.All.Contains(period) && AiLimitMeasures.All.Contains(measure) && (input.Kind == AiLimitKinds.Account) == (input.AccountId is not null));
        if (!shaped)
        {
            throw new UserFriendlyException("A limit is total, person, account (with an account) or group (with a group), of cost, tokens or requests, over a minute, day, "
                + "week or month, and not below nothing; or call-tokens, the most tokens one reply may have (a whole number, on the site, a scope or an account), "
                + "with no period.", AiErrorCodes.Invalid);
        }

        return await aiDb.RunAsync(async db =>
        {
            AiLimit? limit = await db.AiLimits.FirstOrDefaultAsync(l => l.ScopeId == scopeId && l.Kind == input.Kind && l.Measure == measure && l.Period == period
                && l.AccountId == input.AccountId && l.GroupId == input.GroupId, ct);
            if (limit is null)
            {
                limit = new AiLimit(Guid.NewGuid(), scopeId, input.Kind, period, input.Amount, input.AccountId, input.GroupId) { Measure = measure };
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
        if (limit is { ScopeId: null, AccountId: { } account, Kind: AiLimitKinds.Account or AiLimitKinds.CallTokens })
        {
            await AccountAdminAsync(actor, account, ct);
        }
        else
        {
            await AdminAsync(actor, limit.ScopeId, ct);
        }

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

    private async Task AccountAdminAsync(AiActor actor, Guid accountId, CancellationToken ct)
    {
        if (!await rights.IsSiteAdminAsync(actor, ct) && !await rights.AdministersAccountAsync(actor, accountId, ct))
        {
            throw new UserFriendlyException("Only the site's admins and this account's admins set its own AI limits.", AiErrorCodes.Forbidden);
        }
    }

    /// <summary>Connections are set here only when AbpAi keeps them.</summary>
    private void KeptHere()
    {
        if (!KeepsConnections)
        {
            throw new UserFriendlyException("This site's AI connections are kept by the application, and set where it keeps them.", AiErrorCodes.HostConnections);
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

        if (site.ScopeAccess == AiScopeAccessModes.Listed && await AiRouter.AccessOfAsync(db, await chains.OfAsync(scopeId, ct), ct) is not { MayConnect: true })
        {
            throw new UserFriendlyException("The site doesn't let this scope set connections of its own.", AiErrorCodes.Forbidden);
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
        Dictionary<string, AiPrice> pricesBefore = connection.Models.Distinct().ToDictionary(m => m, connection.PriceOf);
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
        connection.CacheWritePrice = input.CacheWritePrice;
        connection.CacheReadPrice = input.CacheReadPrice;
        if (input.IdleTimeoutSeconds is { } idle)
        {
            connection.IdleTimeoutSeconds = idle == 0 ? null : idle is >= 1 and <= 3600 ? idle
                : throw new UserFriendlyException("A connection's idle timeout is from 1 second to an hour (0: the site's).", AiErrorCodes.Invalid);
        }

        if (input.TotalTimeoutSeconds is { } total)
        {
            connection.TotalTimeoutSeconds = total == 0 ? null : total is >= 1 and <= 86_400 ? total
                : throw new UserFriendlyException("A connection's total timeout is from 1 second to a day (0: the site's).", AiErrorCodes.Invalid);
        }

        // Settings only for the models it offers; a model whose price changed has it said when.
        Dictionary<string, AiModelSettings> settings = input.ModelSettings is { } given ? CheckedSettings(connection, given) : connection.ModelSettings;
        DateTime now = DateTime.UtcNow;
        connection.ModelSettings = connection.Models.Where(settings.ContainsKey).ToDictionary(m => m, m =>
            connection.PriceOf(m) != pricesBefore.GetValueOrDefault(m, new AiPrice(0, 0)) ? settings[m] with { PriceSetAt = now } : settings[m]);
    }

    /// <summary>Model settings, checked: each for a model the connection offers, with a name no other of its models has.</summary>
    private static Dictionary<string, AiModelSettings> CheckedSettings(AiConnection connection, IReadOnlyDictionary<string, AiModelSettings> given)
    {
        Dictionary<string, AiModelSettings> settings = [];
        foreach ((string model, AiModelSettings value) in given)
        {
            if (!connection.Models.Contains(model))
            {
                throw new UserFriendlyException($"There are settings for {model}, which {connection.Name} doesn't offer.", AiErrorCodes.Invalid);
            }

            string? name = string.IsNullOrWhiteSpace(value.Name) ? null : value.Name.Trim();
            if (name?.Length > 200 || value.ContextTokens < 1)
            {
                throw new UserFriendlyException($"{model}'s name is at most 200 characters, and its context holds at least one token.", AiErrorCodes.Invalid);
            }

            settings[model] = value with { Name = name };
        }

        List<string> names = [.. connection.Models.Select(m => settings.TryGetValue(m, out AiModelSettings? s) && s.Name is { } n ? n : m)];
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count)
        {
            throw new UserFriendlyException($"Each of {connection.Name}'s models has a name of its own.", AiErrorCodes.Invalid);
        }

        return settings;
    }

    /// <summary>What a connection's provider says it offers; refused when the provider can't list its models or fails to.</summary>
    private async Task<IReadOnlyList<string>> OfferedAsync(AiConnectionDescriptor connection, CancellationToken ct)
    {
        IAiProvider provider = providers.LastOrDefault(p => string.Equals(p.Kind, connection.Provider, StringComparison.OrdinalIgnoreCase))
            ?? throw new UserFriendlyException($"No provider \"{connection.Provider}\" is installed for {connection.Name}.", AiErrorCodes.Invalid);
        IReadOnlyList<string>? offered;
        try
        {
            AiTarget target = new(connection.Id, connection.Name, connection.Endpoint, await source.KeyAsync(connection.Id, ct), connection.Models.FirstOrDefault() ?? "",
                connection.Settings);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Value.ProviderTimeout);
            offered = await provider.ListModelsAsync(target, timeout.Token);
        }
        catch (Exception ex) when (ex is not (UserFriendlyException or OperationCanceledException) || !ct.IsCancellationRequested && ex is OperationCanceledException)
        {
            throw new UserFriendlyException($"{connection.Name} couldn't list its models: {ex.Message}", AiErrorCodes.Invalid, innerException: ex);
        }

        return offered ?? throw new UserFriendlyException($"{connection.Name}'s provider can't list its models: set them by hand.", AiErrorCodes.Invalid);
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

    private AiSiteSettings Info(AiSite site) => new(site.ScopesMayConnect, site.KeepPrompts, site.PromptDays, site.ScopeDataClasses, rules.Classes)
    {
        ScopeAccess = site.ScopeAccess,
    };

    private static AiScopeAccessInfo Info(Guid scopeId, AiScopeAccess? access) =>
        access is null ? new(scopeId, null, false, [], false) : new(scopeId, access.Id, access.AllSiteModels, access.SiteModels, access.MayConnect);

    private static AiConnectionInfo Info(AiConnection c) => Info(StoredAiConnections.Describe(c));

    private static AiConnectionInfo Info(AiConnectionDescriptor c) => new(c.Id, c.ScopeId, c.Name, c.Provider, c.Endpoint, c.HasKey, c.Models, c.DataClasses,
        c.Labels, c.Settings, c.InputPrice, c.OutputPrice, c.ModelPrices, c.StructuredOutput, c.Enabled)
    {
        ModelSettings = c.ModelSettings,
        CacheWritePrice = c.CacheWritePrice,
        CacheReadPrice = c.CacheReadPrice,
        IdleTimeoutSeconds = c.IdleTimeout is { } idle ? (int)idle.TotalSeconds : null,
        TotalTimeoutSeconds = c.TotalTimeout is { } total ? (int)total.TotalSeconds : null,
        Type = c.Type,
    };

    private static AiAliasInfo Info(AiAlias a) => new(a.Id, a.ScopeId, a.Name, a.Targets, a.UpdatedAt);

    private static AiLimitInfo Info(AiLimit l) => new(l.Id, l.ScopeId, l.Kind, l.Period, l.Amount, l.AccountId, l.GroupId) { Measure = l.Measure };
}

internal static class AiConnectionSaves
{
    /// <summary>A connection just saved, as an admin reads it: tested once its save is done, when the input asks.</summary>
    public static async Task<AiConnectionInfo> ContinueWithTestAsync(this Task<AiConnection> saving, AiAdminService admin, AiConnectionInput input, CancellationToken ct) =>
        await admin.SavedAsync(await saving, input, ct);
}
