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
/// A host's own provider, of a kind with the host's prefix (<c>acme:bedrock</c>): answers in words and keeps each call.
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
}
