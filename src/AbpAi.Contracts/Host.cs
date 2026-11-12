namespace AbpAi;

/// <summary>Who's acting on the admin and usage services: an account id, and the host's own principal, never read by AbpAi.</summary>
public sealed record AiActor(Guid Id, object? Principal = null);

/// <summary>A scope as the host names it: its key (<c>FIN</c>, <c>sales-models</c>) and a title.</summary>
public sealed record AiScopeInfo(Guid Id, string Key, string Title);

/// <summary>
/// The host's scopes (a project, a model set, an org unit). Scopes may nest: a call counts against its scope and every
/// scope above it, and an alias or connection set on a scope applies to the scopes under it unless they set their own.
/// The default has one level: every scope's parent is the site.
/// </summary>
public interface IAiScopes
{
    /// <summary>The scope's parent; null when it's at the top (its parent is the site).</summary>
    Task<Guid?> ParentOfAsync(Guid scopeId, CancellationToken cancellationToken);

    /// <summary>The scopes by id, as the host names them; ids it doesn't know are left out.</summary>
    Task<IReadOnlyDictionary<Guid, AiScopeInfo>> GetManyAsync(IEnumerable<Guid> scopeIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, AiScopeInfo>>(new Dictionary<Guid, AiScopeInfo>());
}

/// <summary>Who may administer what. The default: no one, so a host must say.</summary>
public interface IAiRights
{
    /// <summary>The actor administers the site's AI: connections, aliases, limits, every scope's usage.</summary>
    Task<bool> IsSiteAdminAsync(AiActor actor, CancellationToken cancellationToken);

    /// <summary>The actor administers the scope's AI, and so the scopes under it.</summary>
    Task<bool> AdministersScopeAsync(AiActor actor, Guid scopeId, CancellationToken cancellationToken);

    /// <summary>
    /// The actor owns the site: the one who may export its AI calls, prompts included, to send to support. The default:
    /// no one, so a host that wants the export says who.
    /// </summary>
    Task<bool> IsSiteOwnerAsync(AiActor actor, CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>
    /// The actor administers the account (an agent's): they read and set its own limits on the site, which count its
    /// calls everywhere. The site's admins always may; the default: no one else.
    /// </summary>
    Task<bool> AdministersAccountAsync(AiActor actor, Guid accountId, CancellationToken cancellationToken) => Task.FromResult(false);
}

/// <summary>Accounts' groups, for per-person limits set on a group. The default: no groups.</summary>
public interface IAiAccounts
{
    Task<IReadOnlyList<Guid>> GroupsOfAsync(Guid accountId, CancellationToken cancellationToken);
}

/// <summary>A limit passed its notice mark (80% by default) with this call: the limit and the call's scope and account.</summary>
public sealed record AiLimitNotice(AiLimitRef Limit, Guid? CallScopeId, Guid? CallAccountId, string Purpose);

/// <summary>Tells admins when a limit nears, and when it's reached. The default writes to the log.</summary>
public interface IAiNotices
{
    Task LimitNearingAsync(AiLimitNotice notice, CancellationToken cancellationToken);

    /// <summary>
    /// A limit is reached: a call took it to its amount, or one was refused by it. Told once a period for each limit, not
    /// for a minute's (a rate that frees within it). The default does nothing, so a host written before it hears as before.
    /// </summary>
    Task LimitReachedAsync(AiLimitNotice notice, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>The gateway's actions an audit log keeps.</summary>
public static class AiAuditActions
{
    /// <summary>The site's owner exported a period's calls (<see cref="AiAuditEntry.TargetId"/> is the export's id).</summary>
    public const string CallsExported = "ai.calls.exported";

    /// <summary>The site's owner downloaded an export.</summary>
    public const string CallsDownloaded = "ai.calls.downloaded";
}

/// <summary>An action for the audit log: what, who, on what, and what else to keep (the period, how many calls).</summary>
public sealed record AiAuditEntry(string Action, AiActor Actor, Guid? TargetId, IReadOnlyDictionary<string, string?> Details);

/// <summary>
/// Where the gateway's audited actions go: the host's audit log. The default writes them to the log. It's told before
/// the action is done, and when it fails the action isn't: nothing is exported that the log doesn't hold.
/// </summary>
public interface IAiAudit
{
    Task RecordAsync(AiAuditEntry entry, CancellationToken cancellationToken);
}
