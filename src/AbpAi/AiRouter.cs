using Microsoft.EntityFrameworkCore;
using Volo.Abp.DependencyInjection;

namespace AbpAi;

/// <summary>Where a call goes: the connection, the model (by the provider's id), where the alias was set, and what the model can do.</summary>
public sealed record AiRoute(string Alias, AiConnectionDescriptor Connection, string Model, string Source, Guid? SetOnScopeId)
{
    /// <summary>What people and aliases call the model.</summary>
    public string ModelName { get; init; } = Model;

    /// <summary>The model takes tools.</summary>
    public bool Tools { get; init; } = true;

    /// <summary>The model takes images.</summary>
    public bool Images { get; init; } = true;

    /// <summary>The model follows a JSON schema for a reply.</summary>
    public bool StructuredOutput { get; init; } = Connection.StructuredOutput;

    public AiChoice ToChoice() => new(Alias, Model, Connection.Provider, Connection.Id, Connection.Name, Source, SetOnScopeId, StructuredOutput,
        Connection.DataClasses, Connection.Labels)
    {
        ModelName = ModelName,
        Tools = Tools,
        Images = Images,
    };
}

/// <summary>What a call needs of a model: to take its tools, to take its images, to follow a schema for its reply.</summary>
public sealed record AiNeeds(bool Tools = false, bool Images = false, bool StructuredOutput = false)
{
    public static readonly AiNeeds None = new();

    public static AiNeeds Of(AiRequest request) =>
        new(request.Tools is { ValueKind: System.Text.Json.JsonValueKind.Array } tools && tools.GetArrayLength() > 0, AiMessages.HasImages(request.Messages),
            request.OutputSchema is not null);

    /// <summary>A model can do what the call needs.</summary>
    public bool MetBy(AiRoute route) => (!Tools || route.Tools) && (!Images || route.Images) && (!StructuredOutput || route.StructuredOutput);

    public override string ToString() =>
        string.Join(" and ", new[] { Tools ? "takes tools" : null, Images ? "takes images" : null, StructuredOutput ? "follows a schema" : null }.OfType<string>());
}

/// <summary>
/// Leads an alias to a model: the alias set on the call's scope, else on the nearest scope above it, else on the site;
/// else a model by its name (or its id) on the nearest connection that offers it. Of the alias's targets, the first
/// whose model is on, is open to the call's scope, can do what the call needs (tools, images, a schema) and may receive
/// the call's data answers; the rest are its fallbacks. Connections come from the <see cref="IAiConnectionSource"/>.
/// When the site lists what each scope may use (<see cref="AiScopeAccessModes.Listed"/>), a call in a scope reaches the
/// site's models its access opens, and its scopes' own connections only when that access lets it connect.
/// </summary>
public sealed class AiRouter(AiDb aiDb, AiDataRules rules, IAiConnectionSource source) : ITransientDependency
{
    /// <summary>As <see cref="ChooseAsync(string, AiScopeChain, string, AiNeeds, CancellationToken)"/>, for a call that may need a schema.</summary>
    public Task<AiRoute> ChooseAsync(string alias, AiScopeChain chain, string dataClass, bool structured, CancellationToken ct) =>
        ChooseAsync(alias, chain, dataClass, new AiNeeds(StructuredOutput: structured), ct);

    public async Task<AiRoute> ChooseAsync(string alias, AiScopeChain chain, string dataClass, AiNeeds needs, CancellationToken ct)
    {
        string name = alias.Trim();
        List<Guid> ids = [.. chain.Up];
        (List<AiAlias> aliases, AiSite? site, AiScopeAccess? access) = await aiDb.RunAsync(async db =>
        {
            List<AiAlias> a = await db.AiAliases.AsNoTracking().Where(x => x.Name == name && (x.ScopeId == null || ids.Contains(x.ScopeId.Value))).ToListAsync(ct);
            AiSite? s = await db.AiSites.AsNoTracking().FirstOrDefaultAsync(x => x.Id == AiSite.TheId, ct);
            AiScopeAccess? r = s?.ScopeAccess == AiScopeAccessModes.Listed ? await AccessOfAsync(db, chain, ct) : null;
            return (a, s, r);
        }, cancellationToken: ct);
        IReadOnlyList<AiConnectionDescriptor> connections = await source.ListAsync(ids, ct);

        // A scope's connections count only while the site lets scopes connect (and, when it lists access, the scope's
        // access does), and receive only what the site lets them now.
        bool listed = site?.ScopeAccess == AiScopeAccessModes.Listed && chain.Scope is not null;
        bool scopesMayConnect = (site?.ScopesMayConnect ?? false) && (!listed || access is { MayConnect: true });
        IReadOnlyList<string> scopeClasses = site?.ScopeDataClasses is { Count: > 0 } set ? set : AiAdminService.DefaultScopeDataClasses;
        Dictionary<Guid, AiConnectionDescriptor> usable = connections
            .Where(c => c.Enabled && chain.Sees(c.ScopeId) && (c.ScopeId is null || scopesMayConnect))
            .Select(c => c.ScopeId is null ? c : c with { DataClasses = [.. c.DataClasses.Where(d => scopeClasses.Contains(d, StringComparer.OrdinalIgnoreCase))] })
            .DistinctBy(c => c.Id)
            .ToDictionary(c => c.Id);

        AiAlias? named = aliases.OrderBy(a => chain.Nearness(a.ScopeId)).FirstOrDefault();
        List<AiRoute> candidates = named is not null
            ? [.. named.Targets.Where(t => usable.ContainsKey(t.ConnectionId))
                .Select(t => usable[t.ConnectionId] is var c && (t.Model is null ? DefaultModel(c) : c.ModelIdOf(t.Model) ?? t.Model) is { Length: > 0 } model
                    ? Route(name, c, model, named.ScopeId is null ? "site" : "scope", named.ScopeId)
                    : null)
                .OfType<AiRoute>()]
            : [.. usable.Values.OrderBy(c => chain.Nearness(c.ScopeId)).ThenBy(c => c.Name)
                .Select(c => ModelCalled(c, name) is { } model ? Route(name, c, model, "model", c.ScopeId) : null)
                .OfType<AiRoute>()];

        List<AiRoute> open = [.. candidates.Where(r => !listed || r.Connection.ScopeId is not null || access?.Opens(r.Connection.Id, r.Model) == true)];
        List<AiRoute> fit = [.. open.Where(needs.MetBy)];
        if (fit.Count == 0)
        {
            string what = needs.ToString();
            string why = named is null && candidates.Count == 0 ? $"No alias or model \"{name}\" is set up here."
                : open.Count == 0 ? $"No model \"{name}\" leads to is open to this scope."
                : $"No model \"{name}\" leads to is on{(what.Length > 0 ? " and " + what : "")}.";
            throw new AiRefusedException(AiRefusalCodes.NoModel, why);
        }

        return fit.FirstOrDefault(r => AiDataRules.Allows(r.Connection, dataClass))
            ?? throw new AiRefusedException(AiRefusalCodes.DataNotAllowed,
                $"\"{name}\" leads only to models that may not receive {dataClass} data ({string.Join(", ", fit.Select(r => r.Connection.Name).Distinct())}).");
    }

    /// <summary>The class a call counts as.</summary>
    public string ClassOf(string? stated) => rules.ClassOf(stated);

    /// <summary>The access that applies in a scope: its own, else the nearest above it; null for the site, or when none is set.</summary>
    internal static async Task<AiScopeAccess?> AccessOfAsync(IAiDbContext db, AiScopeChain chain, CancellationToken ct)
    {
        if (chain.Scope is null)
        {
            return null;
        }

        List<Guid> ids = [.. chain.Up];
        List<AiScopeAccess> rows = await db.AiScopeAccess.AsNoTracking().Where(a => ids.Contains(a.Id)).ToListAsync(ct);
        return rows.OrderBy(a => chain.Nearness(a.Id)).FirstOrDefault();
    }

    /// <summary>A connection's first model that's on.</summary>
    internal static string? DefaultModel(AiConnectionDescriptor c) => c.Models.FirstOrDefault(m => c.SettingsOf(m) is not { Enabled: false });

    /// <summary>The model a call names by its id or its name, when it's on: by its id first.</summary>
    private static string? ModelCalled(AiConnectionDescriptor c, string name) =>
        c.Models.Where(m => c.SettingsOf(m) is not { Enabled: false }).OrderBy(m => m == name ? 0 : 1).FirstOrDefault(m => m == name || c.NameOf(m) == name);

    /// <summary>
    /// The route to a model, with what it can do; null when it's off. A model with no settings is on and takes
    /// everything; one with settings takes what they say, and follows a schema as they say, else as its connection does.
    /// </summary>
    private static AiRoute? Route(string alias, AiConnectionDescriptor c, string model, string where, Guid? setOn)
    {
        AiModelSettings? settings = c.SettingsOf(model);
        return settings is { Enabled: false }
            ? null
            : new AiRoute(alias, c, model, where, setOn)
            {
                ModelName = c.NameOf(model),
                Tools = settings?.Tools ?? true,
                Images = settings?.Images ?? true,
                StructuredOutput = settings?.StructuredOutput ?? c.StructuredOutput,
            };
    }
}
