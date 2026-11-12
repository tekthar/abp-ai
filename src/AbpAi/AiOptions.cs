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

    /// <summary>
    /// The longest a whole call may take (its total timeout), and a provider to list its models, for a connection that
    /// doesn't set its own.
    /// </summary>
    public TimeSpan ProviderTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The longest wait for the next part of a reply (its idle timeout) for a connection that doesn't set its own: a
    /// streamed call's first event, and each one after it. A call that isn't streamed gets its reply whole, so only its
    /// total timeout applies to it. A connection test waits this long for its answer.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How many times the built-in providers try a call again when the provider is busy or failed for a moment (429, 500,
    /// 502, 503, 504, 529), within its total timeout and never once a stream has begun. 2 by default; 0 tries once.
    /// </summary>
    public int ProviderRetries { get; set; } = 2;

    /// <summary>
    /// What models can do (tools, images, a schema) decides which may answer a call. A host whose models' flags are only
    /// for people to read sets it false, and the router chooses by alias, access and whether a model is on.
    /// </summary>
    public bool CapabilitiesDecide { get; set; } = true;

    /// <summary>How long an export of calls is kept before it's deleted: 7 days.</summary>
    public int ExportDays { get; set; } = 7;

    /// <summary>The longest period one export of calls covers: 31 days.</summary>
    public int MaxExportDays { get; set; } = 31;

    /// <summary>The most calls one export holds.</summary>
    public int MaxExportCalls { get; set; } = 100_000;

    /// <summary>The most bytes one export holds, gzipped: 200 MB.</summary>
    public long MaxExportBytes { get; set; } = 200L * 1024 * 1024;

    /// <summary>
    /// The demo model's pause between the pieces of a streamed reply, so a live view can be seen being written; none by
    /// default, so tests run at once.
    /// </summary>
    public TimeSpan DemoPacing { get; set; } = TimeSpan.Zero;

    /// <summary>The longest a scope chain may be; deeper (or a loop) is the host's mistake and refused.</summary>
    public int MaxScopeDepth { get; set; } = 32;
}
