namespace AbpAi;

/// <summary>
/// The data classes AbpAi starts with, lowest to highest. A site may define its own (<c>AbpAi:DataClasses</c>), in order;
/// a call states its highest, and each connection lists the ones it may receive.
/// </summary>
public static class AiDataClasses
{
    /// <summary>Anything anyone may see.</summary>
    public const string Public = "public";

    /// <summary>The organization's own words: names of things, descriptions, questions.</summary>
    public const string Internal = "internal";

    /// <summary>Totals, counts and averages over the organization's data, with no row of it.</summary>
    public const string Aggregates = "aggregates";

    /// <summary>Rows of the organization's data.</summary>
    public const string Rows = "rows";

    /// <summary>Columns marked sensitive: personal data, pay, health.</summary>
    public const string Sensitive = "sensitive";

    public static readonly IReadOnlyList<string> Default = [Public, Internal, Aggregates, Rows, Sensitive];
}
