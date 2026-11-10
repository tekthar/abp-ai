using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace AbpAi;

/// <summary>
/// Which data may go to which connection. A call states its highest class; a connection lists the classes it may
/// receive. A call that states none, or a class the site doesn't define, counts as the site's highest.
/// </summary>
public sealed class AiDataRules(IOptions<AiOptions> options) : ISingletonDependency
{
    private readonly List<string> _classes = options.Value.DataClasses is { Count: > 0 } classes ? classes : [.. AiDataClasses.Default];

    /// <summary>The site's classes, lowest to highest.</summary>
    public IReadOnlyList<string> Classes => _classes;

    /// <summary>The class a call counts as: its own when the site defines it, else the highest.</summary>
    public string ClassOf(string? stated) =>
        stated is not null && _classes.FirstOrDefault(c => string.Equals(c, stated.Trim(), StringComparison.OrdinalIgnoreCase)) is { } known ? known : _classes[^1];

    /// <summary>The connection may receive the class.</summary>
    public static bool Allows(AiConnection connection, string dataClass) =>
        connection.DataClasses.Any(c => string.Equals(c, dataClass, StringComparison.OrdinalIgnoreCase));

    /// <summary>The classes, checked: each one the site defines.</summary>
    public List<string> Checked(IEnumerable<string>? classes)
    {
        List<string> result = [];
        foreach (string c in classes ?? [])
        {
            string? known = _classes.FirstOrDefault(k => string.Equals(k, c?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (known is null)
            {
                throw new Volo.Abp.UserFriendlyException($"\"{c}\" isn't one of the site's data classes ({string.Join(", ", _classes)}).",
                    AiErrorCodes.Invalid);
            }

            if (!result.Contains(known))
            {
                result.Add(known);
            }
        }

        return result;
    }
}

/// <summary>A scope and the scopes above it: the call's own first, the top last. Empty for the site.</summary>
public sealed record AiScopeChain(IReadOnlyList<Guid> Up)
{
    public static readonly AiScopeChain Site = new([]);

    public Guid? Scope => Up.Count > 0 ? Up[0] : null;

    /// <summary>From the top down, as the ledger keeps it.</summary>
    public Guid[] Path => [.. Up.Reverse()];

    /// <summary>How near a scope is: 0 for the call's own, then up; the site after every scope; -1 when it's not in the chain.</summary>
    public int Nearness(Guid? scopeId) => scopeId is null ? Up.Count : Up.ToList().IndexOf(scopeId.Value);

    public bool Sees(Guid? scopeId) => Nearness(scopeId) >= 0;
}

/// <summary>Walks a scope up to the top through the host's <see cref="IAiScopes"/>.</summary>
public sealed class AiScopeChains(IAiScopes scopes, IOptions<AiOptions> options) : ITransientDependency
{
    public async Task<AiScopeChain> OfAsync(Guid? scopeId, CancellationToken ct)
    {
        if (scopeId is null)
        {
            return AiScopeChain.Site;
        }

        List<Guid> up = [scopeId.Value];
        Guid? parent = await scopes.ParentOfAsync(scopeId.Value, ct);
        while (parent is not null)
        {
            if (up.Contains(parent.Value) || up.Count >= options.Value.MaxScopeDepth)
            {
                throw new InvalidOperationException($"The host's scopes loop or nest deeper than {options.Value.MaxScopeDepth} above {scopeId}.");
            }

            up.Add(parent.Value);
            parent = await scopes.ParentOfAsync(parent.Value, ct);
        }

        return new AiScopeChain(up);
    }
}

/// <summary>The gateway's error codes for its admin and usage services, which a host answers with their HTTP status.</summary>
public static class AiErrorCodes
{
    /// <summary>The actor may not. 403.</summary>
    public const string Forbidden = "AbpAi:Forbidden";

    /// <summary>The input isn't right. 400.</summary>
    public const string Invalid = "AbpAi:Invalid";
}
