using System.Collections.Concurrent;

namespace AbpAi.Host.Tests;

/// <summary>
/// The host test classes, run one after another: each starts the sample on a database of its own, named through the
/// process's environment as the sample reads it.
/// </summary>
public static class HostCollection
{
    public const string Name = "AbpAi host";
}

/// <summary>
/// A host's own provider, of a kind with the host's prefix (<c>acme:bedrock</c>): answers in words, keeps each call, and lists
/// two models when it has a key.
/// </summary>
public sealed class AcmeProvider : IAiProvider
{
    public const string TheKind = "acme:bedrock";

    private readonly ConcurrentQueue<AiProviderCall> _calls = new();

    public string Kind => TheKind;

    /// <summary>Every call it was asked, in order.</summary>
    public IReadOnlyList<AiProviderCall> Calls => [.. _calls];

    public Task<AiProviderReply> CompleteAsync(AiProviderCall call, CancellationToken cancellationToken)
    {
        _calls.Enqueue(call);
        return Task.FromResult(new AiProviderReply(AiMessages.AssistantText("From the host's provider."), 10, Math.Min(5, call.MaxTokens)));
    }

    public IAsyncEnumerable<AiStreamEvent> StreamAsync(AiProviderCall call, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>?> ListModelsAsync(AiTarget target, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>?>(target.ApiKey is null ? null : ["acme-large", "acme-small"]);
}

/// <summary>
/// A provider that's slow: as many seconds before its first event as its model's name says (<c>first-3</c>), then its
/// start; a model called <c>stall</c> stops after its start and sends nothing more. Not streamed, it answers after that
/// long.
/// </summary>
public sealed class SlowProvider : IAiProvider
{
    public const string TheKind = "slow";

    public string Kind => TheKind;

    private static TimeSpan FirstOf(string model) =>
        model.StartsWith("first-", StringComparison.Ordinal) ? TimeSpan.FromSeconds(double.Parse(model[6..], System.Globalization.CultureInfo.InvariantCulture)) : TimeSpan.Zero;

    public async Task<AiProviderReply> CompleteAsync(AiProviderCall call, CancellationToken cancellationToken)
    {
        await Task.Delay(FirstOf(call.Target.Model), cancellationToken);
        return new AiProviderReply(AiMessages.AssistantText("At last."), 10, 3);
    }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiProviderCall call, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(FirstOf(call.Target.Model), cancellationToken);
        AiProviderReply reply = new(AiMessages.AssistantText("At last."), 10, 3);
        foreach (AiStreamEvent e in Providers.DemoProvider.Events(reply, call.Target.Model))
        {
            yield return e;
            if (call.Target.Model == "stall")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }
    }
}

/// <summary>
/// A provider that fails as a real one does, through the built-in providers' reading of an error: a wrong key (anything
/// but <c>good-key</c>) answers 401 with the key in its words, a model it doesn't have answers 404.
/// </summary>
public sealed class FussyProvider : IAiProvider
{
    public const string TheKind = "fussy";

    public string Kind => TheKind;

    public Task<AiProviderReply> CompleteAsync(AiProviderCall call, CancellationToken cancellationToken)
    {
        if (call.Target.ApiKey != "good-key")
        {
            throw Providers.ProviderHttp.Failed("Fussy", 401, Error("authentication_error", $"invalid x-api-key: {call.Target.ApiKey}"), call.Target.ApiKey);
        }

        if (call.Target.Model != "fussy-1")
        {
            throw Providers.ProviderHttp.Failed("Fussy", 404, Error("not_found_error", $"model: {call.Target.Model}"), call.Target.ApiKey);
        }

        return Task.FromResult(new AiProviderReply(AiMessages.AssistantText("OK"), 5, 1));
    }

    public IAsyncEnumerable<AiStreamEvent> StreamAsync(AiProviderCall call, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>An error's body as Anthropic writes one.</summary>
    private static string Error(string type, string message) =>
        System.Text.Json.JsonSerializer.Serialize(new { type = "error", error = new { type, message } });
}

/// <summary>A provider whose replies read most of their input from its cache: 1,000 written, 2,000 read, 100 more in and 50 out.</summary>
public sealed class CachingProvider : IAiProvider
{
    public const string TheKind = "caching";

    public string Kind => TheKind;

    /// <summary>Whether each call asked to be cached.</summary>
    public ConcurrentQueue<bool> Cached { get; } = new();

    public Task<AiProviderReply> CompleteAsync(AiProviderCall call, CancellationToken cancellationToken)
    {
        Cached.Enqueue(call.Cache);
        return Task.FromResult(new AiProviderReply(AiMessages.AssistantText("Cached."), 100, 50) { CacheWriteTokens = 1000, CacheReadTokens = 2000 });
    }

    public IAsyncEnumerable<AiStreamEvent> StreamAsync(AiProviderCall call, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>A provider whose model gives vectors of 8 places whatever size a call asks for, as a model that can't shorten them does.</summary>
public sealed class FixedSizeEmbedder : IAiProvider
{
    public const string TheKind = "fixed-embed";

    public string Kind => TheKind;

    public bool CanEmbed => true;

    public Task<AiProviderEmbedding> EmbedAsync(AiEmbedCall call, CancellationToken cancellationToken) =>
        Task.FromResult(new AiProviderEmbedding([.. call.Inputs.Select(_ => new float[8])], call.Inputs.Count));

    public Task<AiProviderReply> CompleteAsync(AiProviderCall call, CancellationToken cancellationToken) => throw new NotSupportedException();

    public IAsyncEnumerable<AiStreamEvent> StreamAsync(AiProviderCall call, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>Keeps what the gateway would have the audit log keep.</summary>
public sealed class RecordingAudit : IAiAudit
{
    public ConcurrentQueue<AiAuditEntry> Entries { get; } = new();

    /// <summary>When set, the log can't take anything.</summary>
    public bool Broken { get; set; }

    public Task RecordAsync(AiAuditEntry entry, CancellationToken cancellationToken)
    {
        if (Broken)
        {
            throw new InvalidOperationException("The audit log is down.");
        }

        Entries.Enqueue(entry);
        return Task.CompletedTask;
    }
}
