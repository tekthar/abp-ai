namespace AbpAi;

/// <summary>Why the gateway refused a call.</summary>
public static class AiRefusalCodes
{
    /// <summary>No model the alias leads to may answer: none is set, none is on, or none supports what the call needs.</summary>
    public const string NoModel = "no_model";

    /// <summary>A limit is reached; <see cref="AiRefusedException.Limit"/> names it and when it frees.</summary>
    public const string OverLimit = "over_limit";

    /// <summary>The provider failed or answered with an error.</summary>
    public const string Provider = "provider";

    /// <summary>No connection the alias leads to may receive the call's class of data.</summary>
    public const string DataNotAllowed = "data_not_allowed";
}

/// <summary>Which limit refused a call: its kind, where, whose, over what period, what it counts, how much, and when it frees.</summary>
/// <param name="Kind"><c>scope</c> (a scope's or the site's total), <c>account</c> (a named account's own), or <c>person</c> (what each person may spend).</param>
/// <param name="ScopeId">The scope it's set on; null for the site.</param>
/// <param name="AccountId">The account it counts, for <c>account</c> and <c>person</c>.</param>
/// <param name="Period"><c>minute</c>, <c>day</c>, <c>week</c> or <c>month</c>.</param>
/// <param name="Amount">How much, in its measure's units.</param>
/// <param name="Spent">How much was spent before the call, in the same units.</param>
public sealed record AiLimitRef(Guid LimitId, string Kind, Guid? ScopeId, Guid? AccountId, string Period, decimal Amount, decimal Spent, DateTime FreesAt)
{
    /// <summary>What it counts: <c>cost</c> (in the connections' price units), <c>tokens</c> (in and out) or <c>requests</c> (calls).</summary>
    public string Measure { get; init; } = "cost";
}

/// <summary>The gateway refused the call before it left (or the provider failed). Nothing was sent when it's a refusal.</summary>
public sealed class AiRefusedException(string code, string message, AiLimitRef? limit = null, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>One of <see cref="AiRefusalCodes"/>.</summary>
    public string Code { get; } = code;

    /// <summary>The limit, for <see cref="AiRefusalCodes.OverLimit"/>.</summary>
    public AiLimitRef? Limit { get; } = limit;
}
