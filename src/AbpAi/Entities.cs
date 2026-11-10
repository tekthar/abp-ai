using Volo.Abp.Domain.Entities;

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

    /// <summary>Its models follow a JSON schema for a reply (<see cref="AiRequest.OutputSchema"/>).</summary>
    public bool StructuredOutput { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; private set; }

    public AiPrice PriceOf(string model) => ModelPrices.TryGetValue(model, out AiPrice? price) ? price : new AiPrice(InputPrice, OutputPrice);
}

/// <summary>A price per million tokens.</summary>
public sealed record AiPrice(decimal Input, decimal Output);

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

    public static readonly IReadOnlyList<string> All = [Total, Person, Account, Group];
}

/// <summary>The periods a limit is over, in UTC: a day, a week from Monday, a month from its first.</summary>
public static class AiPeriods
{
    public const string Day = "day";
    public const string Week = "week";
    public const string Month = "month";

    public static readonly IReadOnlyList<string> All = [Day, Week, Month];

    public static (DateTime Start, DateTime End) Of(string period, DateTime now)
    {
        DateTime day = DateTime.SpecifyKind(now.Date, DateTimeKind.Utc);
        DateTime monday = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        DateTime first = new(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return period switch
        {
            Day => (day, day.AddDays(1)),
            Week => (monday, monday.AddDays(7)),
            Month => (first, first.AddMonths(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(period), period, "A period is day, week or month."),
        };
    }
}

/// <summary>A limit on spending (in the connections' price units) over a period, on the site or a scope.</summary>
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

    /// <summary>One of <see cref="AiPeriods"/>.</summary>
    public string Period { get; private set; } = "";

    public decimal Amount { get; set; }

    public DateTime CreatedAt { get; private set; }
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
        Purpose = purpose;
        Alias = alias;
        ConnectionId = connectionId;
        Connection = connection;
        Provider = provider;
        Model = model;
        DataClass = dataClass;
        Reserved = reserved;
        Cost = reserved;
        Streamed = streamed;
    }

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

    /// <summary>What it cost: the estimate while reserved, then the cost, or nothing when it failed.</summary>
    public decimal Cost { get; set; }

    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public int? DurationMs { get; set; }

    public string? Error { get; set; }
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

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
