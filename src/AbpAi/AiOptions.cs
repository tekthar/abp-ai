namespace AbpAi;

/// <summary>The gateway's configuration, from the <c>AbpAi</c> section.</summary>
public class AiOptions
{
    public const string Section = "AbpAi";

    /// <summary>
    /// The site's data classes, lowest to highest. A call that states none, or one not here, counts as the last: the
    /// gateway fails closed.
    /// </summary>
    public List<string> DataClasses { get; set; } = [.. AiDataClasses.Default];

    /// <summary>When admins hear that a limit nears: its share spent. 0.8 by default.</summary>
    public decimal NoticeAt { get; set; } = 0.8m;

    /// <summary>How long a reservation holds when no one settles it (the server stopped mid-call).</summary>
    public TimeSpan ReservationTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Characters per token, for a call's estimate before it leaves.</summary>
    public int CharsPerToken { get; set; } = 4;

    /// <summary>How often stale reservations are let go and kept prompts past their period deleted.</summary>
    public TimeSpan HousekeepingEvery { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a provider may take for one call.</summary>
    public TimeSpan ProviderTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The longest a scope chain may be; deeper (or a loop) is the host's mistake and refused.</summary>
    public int MaxScopeDepth { get; set; } = 32;
}
