using Microsoft.EntityFrameworkCore;
using Volo.Abp.DependencyInjection;

namespace AbpAi;

/// <summary>Where a call goes: the connection, the model, and where the alias was set.</summary>
public sealed record AiRoute(string Alias, AiConnectionDescriptor Connection, string Model, string Source, Guid? SetOnScopeId)
{
    /// <summary>The model follows a JSON schema for a reply.</summary>
    public bool StructuredOutput { get; init; } = Connection.StructuredOutput;

    public AiChoice ToChoice() => new(Alias, Model, Connection.Provider, Connection.Id, Connection.Name, Source, SetOnScopeId, StructuredOutput,
        Connection.DataClasses, Connection.Labels);
}

/// <summary>What a call needs of a model: to follow a schema for its reply.</summary>
public sealed record AiNeeds(bool StructuredOutput = false)
{
    public static readonly AiNeeds None = new();

    public static AiNeeds Of(AiRequest request) => new(request.OutputSchema is not null);
}

/// <summary>
/// Leads an alias to a model: the alias set on the call's scope, else on the nearest scope above it, else on the site;
/// else a model by its name on the nearest connection that offers it. Of the alias's targets, the first that is on, may
/// receive the call's data, and (when asked) follows a schema; the rest are its fallbacks. Connections come from the
/// <see cref="IAiConnectionSource"/>.
/// </summary>
public sealed class AiRouter(AiDb aiDb, AiDataRules rules, IAiConnectionSource source) : ITransientDependency
{
    /// <summary>As <see cref="ChooseAsync(string, AiScopeChain, string, AiNeeds, CancellationToken)"/>, for a call that may need a schema.</summary>
    public Task<AiRoute> ChooseAsync(string alias, AiScopeChain chain, string dataClass, bool structured, CancellationToken ct) =>
        ChooseAsync(alias, chain, dataClass, new AiNeeds(structured), ct);

    public async Task<AiRoute> ChooseAsync(string alias, AiScopeChain chain, string dataClass, AiNeeds needs, CancellationToken ct)
    {
        string name = alias.Trim();
        List<Guid> ids = [.. chain.Up];
        (List<AiAlias> aliases, AiSite? site) = await aiDb.RunAsync(async db =>
        {
            List<AiAlias> a = await db.AiAliases.AsNoTracking().Where(x => x.Name == name && (x.ScopeId == null || ids.Contains(x.ScopeId.Value))).ToListAsync(ct);
            AiSite? s = await db.AiSites.AsNoTracking().FirstOrDefaultAsync(x => x.Id == AiSite.TheId, ct);
            return (a, s);
        }, cancellationToken: ct);
        IReadOnlyList<AiConnectionDescriptor> connections = await source.ListAsync(ids, ct);

        // A scope's connections count only while the site lets scopes connect, and receive only what the site lets them now.
        bool scopesMayConnect = site?.ScopesMayConnect ?? false;
        IReadOnlyList<string> scopeClasses = site?.ScopeDataClasses is { Count: > 0 } set ? set : AiAdminService.DefaultScopeDataClasses;
        Dictionary<Guid, AiConnectionDescriptor> usable = connections
            .Where(c => c.Enabled && chain.Sees(c.ScopeId) && (c.ScopeId is null || scopesMayConnect))
            .Select(c => c.ScopeId is null ? c : c with { DataClasses = [.. c.DataClasses.Where(d => scopeClasses.Contains(d, StringComparer.OrdinalIgnoreCase))] })
            .DistinctBy(c => c.Id)
            .ToDictionary(c => c.Id);

        AiAlias? named = aliases.OrderBy(a => chain.Nearness(a.ScopeId)).FirstOrDefault();
        List<AiRoute> candidates = named is not null
            ? [.. named.Targets.Where(t => usable.ContainsKey(t.ConnectionId))
                .Select(t => usable[t.ConnectionId] is var c && (t.Model ?? c.Models.FirstOrDefault()) is { Length: > 0 } model
                    ? new AiRoute(name, c, model, named.ScopeId is null ? "site" : "scope", named.ScopeId)
                    : null)
                .OfType<AiRoute>()]
            : [.. usable.Values.Where(c => c.Models.Contains(name, StringComparer.Ordinal)).OrderBy(c => chain.Nearness(c.ScopeId)).ThenBy(c => c.Name)
                .Select(c => new AiRoute(name, c, name, "model", c.ScopeId))];

        if (needs.StructuredOutput)
        {
            candidates = [.. candidates.Where(r => r.StructuredOutput)];
        }

        if (candidates.Count == 0)
        {
            string why = named is null ? $"No alias or model \"{name}\" is set up here." : $"No model \"{name}\" leads to is on{(needs.StructuredOutput ? " and follows a schema" : "")}.";
            throw new AiRefusedException(AiRefusalCodes.NoModel, why);
        }

        return candidates.FirstOrDefault(r => AiDataRules.Allows(r.Connection, dataClass))
            ?? throw new AiRefusedException(AiRefusalCodes.DataNotAllowed,
                $"\"{name}\" leads only to models that may not receive {dataClass} data ({string.Join(", ", candidates.Select(r => r.Connection.Name).Distinct())}).");
    }

    /// <summary>The class a call counts as.</summary>
    public string ClassOf(string? stated) => rules.ClassOf(stated);
}
