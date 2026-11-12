using System.Runtime.CompilerServices;
using Volo.Abp.Domain.Entities;

// AiPrice moved to AbpAi.Contracts in 0.3.0, so a host's connection source can name prices; code built against 0.2.0 finds it there.
[assembly: TypeForwardedTo(typeof(AbpAi.AiPrice))]

namespace AbpAi;

/// <summary>
/// A connection to a provider: for the site (no scope) or for a scope when the site lets scopes connect. Its key is
/// kept encrypted and never shown. It lists the data classes it may receive and labels for where it runs.
/// </summary>
public class AiConnection : Entity<Guid>
{
    protected AiConnection()
    {
    }

    public AiConnection(Guid id, Guid? scopeId, string name, string provider) : base(id)
    {
        ScopeId = scopeId;
        Name = name;
        Provider = provider;
        CreatedAt = DateTime.UtcNow;
    }

    /// <summary>The scope it's set on; null for the site. It applies to the scopes under it too.</summary>
    public Guid? ScopeId { get; private set; }

    public string Name { get; set; } = "";

    /// <summary>The provider's kind: <c>anthropic</c>, <c>openai</c>, <c>demo</c>, or one a host registers.</summary>
    public string Provider { get; set; } = "";

    /// <summary>The provider's address; empty for its usual one.</summary>
    public string Endpoint { get; set; } = "";

    /// <summary>The key, encrypted with the site's string encryption; null when the provider needs none.</summary>
    public string? ProtectedKey { get; set; }

    /// <summary>The models it offers; the first is its default. A call naming one of these by name may reach it.</summary>
    public List<string> Models { get; set; } = [];

    /// <summary>
    /// Its models' settings, by the provider's ids: a name to call it by, whether it's on, and what it can do. A model
    /// with none is on and takes everything, as every model did before models had settings.
    /// </summary>
    public Dictionary<string, AiModelSettings> ModelSettings { get; set; } = [];

    /// <summary>The data classes it may receive. A call whose class isn't here is refused before it leaves.</summary>
    public List<string> DataClasses { get; set; } = [];

    /// <summary>Where it runs, for people to read and rules to match: <c>on-premises</c>, <c>eu</c>, <c>provider-cloud</c>.</summary>
    public List<string> Labels { get; set; } = [];

    /// <summary>The provider's own settings: <c>anthropic-version</c>, <c>api-version</c>, <c>auth</c>, <c>max-tokens-field</c>.</summary>
    public Dictionary<string, string> Settings { get; set; } = [];

    /// <summary>Price per million input tokens, for its models unless <see cref="ModelPrices"/> names one.</summary>
    public decimal InputPrice { get; set; }

    /// <summary>Price per million output tokens.</summary>
    public decimal OutputPrice { get; set; }

    /// <summary>A model's own prices: its name, then input and output per million.</summary>
    public Dictionary<string, AiPrice> ModelPrices { get; set; } = [];

    /// <summary>Per million input tokens written to the provider's cache; null: 1.25 times the input price.</summary>
    public decimal? CacheWritePrice { get; set; }

    /// <summary>Per million input tokens read from the provider's cache; null: a tenth of the input price.</summary>
    public decimal? CacheReadPrice { get; set; }

    /// <summary>The longest wait, in seconds, for the next part of a reply through it; null: the site's.</summary>
    public int? IdleTimeoutSeconds { get; set; }

    /// <summary>The longest, in seconds, a whole call through it may take; null: the site's.</summary>
    public int? TotalTimeoutSeconds { get; set; }

    /// <summary>Its models follow a JSON schema for a reply (<see cref="AiRequest.OutputSchema"/>).</summary>
    public bool StructuredOutput { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; private set; }

    public AiPrice PriceOf(string model) => ModelPrices.TryGetValue(model, out AiPrice? price)
        ? price
        : new AiPrice(InputPrice, OutputPrice) { CacheWrite = CacheWritePrice, CacheRead = CacheReadPrice };
}

/// <summary>An alias's target: a connection and one of its models (null: the connection's first).</summary>
public sealed record AiAliasTarget(Guid ConnectionId, string? Model = null);

/// <summary>
/// An alias set on the site or a scope: the targets it leads to, in order. A call goes to the first that may receive
/// its data (and follows its schema, when it gives one); the rest are its fallbacks.
/// </summary>
public class AiAlias : Entity<Guid>
{
    protected AiAlias()
    {
    }

    public AiAlias(Guid id, Guid? scopeId, string name) : base(id)
    {
        ScopeId = scopeId;
        Name = name;
    }

    public Guid? ScopeId { get; private set; }

    public string Name { get; private set; } = "";

    public List<AiAliasTarget> Targets { get; set; } = [];

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The kinds of limit.</summary>
public static class AiLimitKinds
{
    /// <summary>The scope's (or the site's) total, every account's calls together.</summary>
    public const string Total = "total";

    /// <summary>What each person may spend, by default, without a row per person.</summary>
    public const string Person = "person";

    /// <summary>A named account's own: an agent's, or a person's in place of <see cref="Person"/>.</summary>
    public const string Account = "account";

    /// <summary>What each member of a group may spend, in place of <see cref="Person"/>; the most generous of a person's groups.</summary>
    public const string Group = "group";

    /// <summary>
    /// The most tokens one reply may have, with no period: set on the site, a scope, or an account (with or without a
    /// scope). A call's <c>MaxTokens</c> is cut to the lowest that applies before it's estimated.
    /// </summary>
    public const string CallTokens = "call-tokens";

    public static readonly IReadOnlyList<string> All = [Total, Person, Account, Group, CallTokens];
}

/// <summary>What a limit counts.</summary>
public static class AiLimitMeasures
{
    /// <summary>What calls cost, in the connections' price units.</summary>
    public const string Cost = "cost";

    /// <summary>Tokens in and out.</summary>
    public const string Tokens = "tokens";

    /// <summary>Calls.</summary>
    public const string Requests = "requests";

    public static readonly IReadOnlyList<string> All = [Cost, Tokens, Requests];
}

/// <summary>The periods a limit is over, in UTC: a minute from its start, a day, a week from Monday, a month from its first.</summary>
public static class AiPeriods
{
    public const string Minute = "minute";
    public const string Day = "day";
    public const string Week = "week";
    public const string Month = "month";

    public static readonly IReadOnlyList<string> All = [Minute, Day, Week, Month];

    public static (DateTime Start, DateTime End) Of(string period, DateTime now)
    {
        DateTime day = DateTime.SpecifyKind(now.Date, DateTimeKind.Utc);
        DateTime monday = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        DateTime first = new(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime minute = new(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
        return period switch
        {
            Minute => (minute, minute.AddMinutes(1)),
            Day => (day, day.AddDays(1)),
            Week => (monday, monday.AddDays(7)),
            Month => (first, first.AddMonths(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(period), period, "A period is minute, day, week or month."),
        };
    }
}

/// <summary>
/// A limit on what calls cost, their tokens or their number (<see cref="Measure"/>) over a period, on the site or a scope;
/// or (<see cref="AiLimitKinds.CallTokens"/>) the most tokens one reply may have.
/// </summary>
public class AiLimit : Entity<Guid>
{
    protected AiLimit()
    {
    }

    public AiLimit(Guid id, Guid? scopeId, string kind, string period, decimal amount, Guid? accountId = null, Guid? groupId = null) : base(id)
    {
        ScopeId = scopeId;
        Kind = kind;
        Period = period;
        Amount = amount;
        AccountId = accountId;
        GroupId = groupId;
        CreatedAt = DateTime.UtcNow;
    }

    public Guid? ScopeId { get; private set; }

    /// <summary>One of <see cref="AiLimitKinds"/>.</summary>
    public string Kind { get; private set; } = "";

    /// <summary>For <see cref="AiLimitKinds.Account"/>.</summary>
    public Guid? AccountId { get; private set; }

    /// <summary>For <see cref="AiLimitKinds.Group"/>.</summary>
    public Guid? GroupId { get; private set; }

    /// <summary>One of <see cref="AiPeriods"/>; empty for <see cref="AiLimitKinds.CallTokens"/>.</summary>
    public string Period { get; private set; } = "";

    /// <summary>What it counts: one of <see cref="AiLimitMeasures"/>, <see cref="AiLimitMeasures.Cost"/> unless it says.</summary>
    public string Measure { get; init; } = AiLimitMeasures.Cost;

    /// <summary>How much, in the measure's units: cost in price units, tokens, or calls.</summary>
    public decimal Amount { get; set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>The start of the period its admins were last told it's reached in: they're told once a period.</summary>
    public DateTime? ReachedNoticeFor { get; set; }
}

/// <summary>The states of a usage record.</summary>
public static class AiUsageStates
{
    /// <summary>The call's estimate is held while it runs.</summary>
    public const string Reserved = "reserved";

    /// <summary>The call answered; its cost is what it cost.</summary>
    public const string Done = "done";

    /// <summary>The provider failed or the call was stopped; it costs nothing.</summary>
    public const string Failed = "failed";

    /// <summary>A reservation no one settled (the server stopped): it no longer holds anything.</summary>
    public const string Abandoned = "abandoned";

    /// <summary>
    /// The gateway refused the call before it left (a limit, no model, the data rules): it costs nothing, and its
    /// <see cref="AiUsageRecord.Code"/> says why.
    /// </summary>
    public const string Refused = "refused";

    public static readonly IReadOnlyList<string> All = [Reserved, Done, Failed, Abandoned, Refused];
}

/// <summary>
/// A call on the usage ledger: reserved with its estimate before it leaves, then settled with what it cost. What asked
/// (<see cref="Purpose"/>), where (the scope and every scope above it), whose, about what, which model, and its data class.
/// </summary>
public class AiUsageRecord : Entity<Guid>
{
    protected AiUsageRecord()
    {
    }

    public AiUsageRecord(Guid id, DateTime at, Guid? scopeId, Guid[] scopePath, Guid? accountId, Guid? onBehalfOf, string purpose, string alias,
        Guid connectionId, string connection, string provider, string model, string dataClass, decimal reserved, bool streamed) : base(id)
    {
        At = at;
        ScopeId = scopeId;
        ScopePath = scopePath;
        AccountId = accountId;
        OnBehalfOf = onBehalfOf;
        // Each cut to its column, so a long name or purpose is kept shortened rather than failing the call.
        Purpose = Cut(purpose, 200);
        Alias = Cut(alias, 100);
        ConnectionId = connectionId;
        Connection = Cut(connection, 100);
        Provider = Cut(provider, 64);
        Model = Cut(model, 200);
        DataClass = Cut(dataClass, 64);
        Reserved = reserved;
        Cost = reserved;
        Streamed = streamed;
    }

    /// <summary>A value as long as its column holds; the call is recorded with it shortened.</summary>
    public static string Cut(string value, int max) => value.Length > max ? value[..max] : value;

    public DateTime At { get; private set; }

    public string State { get; set; } = AiUsageStates.Reserved;

    public Guid? ScopeId { get; private set; }

    /// <summary>The call's scope and every scope above it, from the top down; empty for the site.</summary>
    public Guid[] ScopePath { get; private set; } = [];

    public Guid? AccountId { get; private set; }

    public Guid? OnBehalfOf { get; private set; }

    public string Purpose { get; private set; } = "";

    public string? AboutKind { get; set; }

    public string? AboutId { get; set; }

    public string Alias { get; private set; } = "";

    public Guid ConnectionId { get; private set; }

    public string Connection { get; private set; } = "";

    public string Provider { get; private set; } = "";

    public string Model { get; private set; } = "";

    public string DataClass { get; private set; } = "";

    public bool Streamed { get; private set; }

    /// <summary>The estimate held before the call.</summary>
    public decimal Reserved { get; private set; }

    /// <summary>The estimate's tokens (in, and all of its <c>MaxTokens</c> out), held against token limits while it's reserved.</summary>
    public int ReservedTokens { get; init; }

    /// <summary>What it cost: the estimate while reserved, then the cost, or nothing when it failed.</summary>
    public decimal Cost { get; set; }

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public int? DurationMs { get; set; }

    public string? Error { get; set; }

    /// <summary>Why a refused or failed call didn't answer: one of <see cref="AiRefusalCodes"/>.</summary>
    public string? Code { get; set; }

    /// <summary>Input tokens written to the provider's cache (not in <see cref="InputTokens"/>).</summary>
    public int CacheWriteTokens { get; set; }

    /// <summary>Input tokens read from the provider's cache (not in <see cref="InputTokens"/>).</summary>
    public int CacheReadTokens { get; set; }

    /// <summary>Every token the call took: in, written to the cache, read from it, and out.</summary>
    public long AllTokens => (long)InputTokens + CacheWriteTokens + CacheReadTokens + OutputTokens;
}

/// <summary>A call's prompt and reply, kept only when the site turns that on, and only for its period.</summary>
public class AiPromptRecord : Entity<Guid>
{
    protected AiPromptRecord()
    {
    }

    public AiPromptRecord(Guid usageId, string? system, string messages, string? reply) : base(usageId)
    {
        System = system;
        Messages = messages;
        Reply = reply;
        At = DateTime.UtcNow;
    }

    public DateTime At { get; private set; }

    public string? System { get; private set; }

    public string Messages { get; private set; } = "";

    public string? Reply { get; set; }
}

/// <summary>How the site's models are open to scopes.</summary>
public static class AiScopeAccessModes
{
    /// <summary>Every scope uses every one of the site's connections; scopes connect their own while the site lets them.</summary>
    public const string Open = "open";

    /// <summary>
    /// A scope uses the site's models only as its access (<see cref="AiScopeAccess"/>, its own or the nearest above it)
    /// lists them, and connections of its own only when the site lets scopes connect and its access lets it.
    /// </summary>
    public const string Listed = "listed";

    public static readonly IReadOnlyList<string> All = [Open, Listed];
}

/// <summary>One of the site's models: its connection, and the provider's id for it.</summary>
public sealed record AiModelRef(Guid ConnectionId, string Model);

/// <summary>
/// Which of the site's models a scope may use, and whether it may connect its own, when the site lists them
/// (<see cref="AiScopeAccessModes.Listed"/>). Its id is the scope's. A scope with none has the nearest one above it;
/// a scope with none anywhere above it may use none of the site's models.
/// </summary>
public class AiScopeAccess : Entity<Guid>
{
    protected AiScopeAccess()
    {
    }

    public AiScopeAccess(Guid scopeId) : base(scopeId)
    {
    }

    /// <summary>Every one of the site's models, the ones added later too.</summary>
    public bool AllSiteModels { get; set; }

    /// <summary>The site's models it may use, when not all of them.</summary>
    public List<AiModelRef> SiteModels { get; set; } = [];

    /// <summary>Its admins may set connections of their own, while the site lets scopes connect.</summary>
    public bool MayConnect { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>It may use the site's model.</summary>
    public bool Opens(Guid connectionId, string model) => AllSiteModels || SiteModels.Contains(new AiModelRef(connectionId, model));
}

/// <summary>The site's AI settings: one row.</summary>
public class AiSite : Entity<Guid>
{
    public static readonly Guid TheId = new("00000000-0000-0000-0000-00000000a1a1");

    protected AiSite()
    {
    }

    public AiSite(Guid id) : base(id)
    {
    }

    /// <summary>Scopes' admins may set connections of their own.</summary>
    public bool ScopesMayConnect { get; set; }

    /// <summary>The gateway keeps prompts and replies, for <see cref="PromptDays"/>.</summary>
    public bool KeepPrompts { get; set; }

    public int PromptDays { get; set; } = 30;

    /// <summary>The data classes a scope's own connections may list at most, so a scope's admin can't widen what leaves.</summary>
    public List<string> ScopeDataClasses { get; set; } = [];

    /// <summary>How scopes use the site's models: <see cref="AiScopeAccessModes.Open"/> (every scope, every model) or <see cref="AiScopeAccessModes.Listed"/>.</summary>
    public string ScopeAccess { get; set; } = AiScopeAccessModes.Open;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An export of a period's calls for support (AI8), made by the site's owner: JSON lines, gzipped, kept for
/// <c>AbpAi:ExportDays</c> (7) and then deleted. A call's prompt and reply are in it only when the site kept them.
/// </summary>
public class AiExport : Entity<Guid>
{
    protected AiExport()
    {
    }

    public AiExport(Guid id, Guid actorId, DateTime from, DateTime to, Guid? scopeId, DateTime expiresAt) : base(id)
    {
        ActorId = actorId;
        From = from;
        To = to;
        ScopeId = scopeId;
        At = DateTime.UtcNow;
        ExpiresAt = expiresAt;
    }

    public DateTime At { get; private set; }

    /// <summary>Who made it.</summary>
    public Guid ActorId { get; private set; }

    public DateTime From { get; private set; }

    public DateTime To { get; private set; }

    /// <summary>The scope it's of (and the scopes under it); null for the whole site.</summary>
    public Guid? ScopeId { get; private set; }

    /// <summary>The calls in it.</summary>
    public int Calls { get; set; }

    /// <summary>Of those, the ones with their prompt and reply.</summary>
    public int Prompts { get; set; }

    /// <summary>It stopped at <c>AbpAi:MaxExportCalls</c> or <c>AbpAi:MaxExportBytes</c>: the period has more calls than it holds.</summary>
    public bool Truncated { get; set; }

    /// <summary>Its size, gzipped.</summary>
    public long Bytes { get; set; }

    public DateTime ExpiresAt { get; private set; }

    /// <summary>The JSON lines, gzipped.</summary>
    public byte[] Content { get; set; } = [];
}
