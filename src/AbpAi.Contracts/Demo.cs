using System.Text.Json;

namespace AbpAi;

/// <summary>
/// Answers for the demo model, so features that use AI run in tests (and evals in CI) with no provider. Each
/// responder registered is asked in turn; the first that answers wins, and when none does the demo model says it's
/// the demo model. A responder returns an assistant message in the Messages API's shape (<see cref="AiMessages"/>
/// helps build one).
/// </summary>
public interface IAiDemoResponder
{
    /// <summary>The reply, or null to let the next responder answer.</summary>
    Task<JsonElement?> RespondAsync(AiProviderCall call, CancellationToken cancellationToken);
}
