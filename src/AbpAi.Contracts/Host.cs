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
}

/// <summary>Accounts' groups, for per-person limits set on a group. The default: no groups.</summary>
public interface IAiAccounts
{
    Task<IReadOnlyList<Guid>> GroupsOfAsync(Guid accountId, CancellationToken cancellationToken);
}

/// <summary>A limit passed its notice mark (80% by default) with this call: the limit and the call's scope and account.</summary>
public sealed record AiLimitNotice(AiLimitRef Limit, Guid? CallScopeId, Guid? CallAccountId, string Purpose);

/// <summary>Tells admins when a limit nears. The default writes to the log.</summary>
public interface IAiNotices
{
    Task LimitNearingAsync(AiLimitNotice notice, CancellationToken cancellationToken);
}
