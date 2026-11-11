using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Security.Encryption;

namespace AbpAi;

/// <summary>
/// The gateway (<see cref="IAi"/>). Each call: the alias leads to a model through the call's scope chain; the data
/// rules choose the first target that may receive the call's class (or refuse, before anything leaves); the ledger
/// reserves the estimate against every limit that applies; the provider answers; the record is settled with what it
/// cost; and the prompt is kept only when the site keeps prompts.
/// </summary>
public sealed class AiService(AiRouter router, AiScopeChains chains, AiLedger ledger, AiDb aiDb, IEnumerable<IAiProvider> providers, IStringEncryptionService encryption,
    IOptions<AiOptions> options, ILogger<AiService> logger) : IAi, ITransientDependency
{
    public async Task<AiChoice> ResolveAsync(string alias, Guid? scopeId = null, string? dataClass = null, CancellationToken cancellationToken = default)
    {
        AiScopeChain chain = await chains.OfAsync(scopeId, cancellationToken);
        AiRoute route = await router.ChooseAsync(alias, chain, router.ClassOf(dataClass), structured: false, cancellationToken);
        return route.ToChoice();
    }

    public async Task<AiReply> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        (AiCall call, IAiProvider provider, AiProviderCall providerCall) = await PrepareAsync(request, streamed: false, cancellationToken);
        AiReservation reservation = await ledger.ReserveAsync(call, cancellationToken);
        Stopwatch watch = Stopwatch.StartNew();
        AiProviderReply answer;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Value.ProviderTimeout);
            answer = await provider.CompleteAsync(providerCall, timeout.Token);
        }
        catch (Exception ex)
        {
            await ledger.FailAsync(reservation, ex.Message, (int)watch.ElapsedMilliseconds);
            throw Refusal(ex, call, cancellationToken);
        }

        return await FinishAsync(call, reservation, answer.Message, answer.InputTokens, answer.OutputTokens, (int)watch.ElapsedMilliseconds, cancellationToken);
    }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        (AiCall call, IAiProvider provider, AiProviderCall providerCall) = await PrepareAsync(request, streamed: true, cancellationToken);
        AiReservation reservation = await ledger.ReserveAsync(call, cancellationToken);
        Stopwatch watch = Stopwatch.StartNew();
        Providers.AiMessageBuilder builder = new();
        IAsyncEnumerator<AiStreamEvent>? events = null;
        bool ended = false;
        try
        {
            events = provider.StreamAsync(providerCall, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                AiStreamEvent e;
                try
                {
                    if (!await events.MoveNextAsync())
                    {
                        break;
                    }

                    e = events.Current;
                    builder.Add(e);
                }
                catch (Exception ex)
                {
                    ended = true;
                    await ledger.FailAsync(reservation, ex.Message, (int)watch.ElapsedMilliseconds);
                    throw Refusal(ex, call, cancellationToken);
                }

                yield return e;
            }

            ended = true;
        }
        finally
        {
            if (events is not null)
            {
                await events.DisposeAsync();
            }

            if (!ended)
            {
                // The caller stopped reading: the call is over, and costs what it had used by then.
                await ledger.SettleAsync(reservation, call, builder.InputTokens, builder.OutputTokens, Cost(call.Route, builder.InputTokens, builder.OutputTokens),
                    (int)watch.ElapsedMilliseconds, CancellationToken.None);
            }
        }

        AiReply reply = await FinishAsync(call, reservation, builder.Message, builder.InputTokens, builder.OutputTokens, (int)watch.ElapsedMilliseconds, cancellationToken);
        yield return new AiStreamEvent(AiStreamEvent.Done, reply.Message) { Reply = reply };
    }

    private async Task<(AiCall Call, IAiProvider Provider, AiProviderCall ProviderCall)> PrepareAsync(AiRequest request, bool streamed, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Alias))
        {
            throw new ArgumentException("A call names an alias or a model.", nameof(request));
        }

        if (request.Messages.ValueKind != JsonValueKind.Array || request.Messages.GetArrayLength() == 0)
        {
            throw new ArgumentException("A call's messages are a JSON array of { role, content }, and not empty.", nameof(request));
        }

        if (request.MaxTokens <= 0)
        {
            throw new ArgumentException("A call's MaxTokens is more than nothing.", nameof(request));
        }

        if (request.SystemBlocks is { ValueKind: not (JsonValueKind.Array or JsonValueKind.String or JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            throw new ArgumentException("A call's SystemBlocks are a JSON array of the Messages API's blocks, or a string.", nameof(request));
        }

        if (request.ProviderOptions is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            throw new ArgumentException("A call's ProviderOptions are a JSON object.", nameof(request));
        }

        AiScopeChain chain = await chains.OfAsync(request.ScopeId, ct);
        string dataClass = router.ClassOf(request.DataClass);
        AiRoute route = await router.ChooseAsync(request.Alias, chain, dataClass, request.OutputSchema is not null, ct);
        IAiProvider provider = providers.LastOrDefault(p => string.Equals(p.Kind, route.Connection.Provider, StringComparison.OrdinalIgnoreCase))
            ?? throw new AiRefusedException(AiRefusalCodes.NoModel, $"No provider \"{route.Connection.Provider}\" is installed for {route.Connection.Name}.");

        AiTarget target = new(route.Connection.Id, route.Connection.Name, route.Connection.Endpoint,
            route.Connection.ProtectedKey is { Length: > 0 } key ? encryption.Decrypt(key) : null, route.Model, route.Connection.Settings);
        AiProviderCall providerCall = new(target, request.Messages, request.System, request.Tools, request.MaxTokens, request.OutputSchema)
        {
            Purpose = request.Purpose,
            ScopeId = request.ScopeId,
            SystemBlocks = Given(request.SystemBlocks),
            ProviderOptions = Given(request.ProviderOptions),
            Request = request,
        };
        return (new AiCall(request, chain, route, dataClass, Estimate(request, route), streamed), provider, providerCall);
    }

    /// <summary>A call's estimate before it leaves: its text's tokens in, and all its MaxTokens out.</summary>
    private decimal Estimate(AiRequest request, AiRoute route)
    {
        int perToken = Math.Max(1, options.Value.CharsPerToken);
        long chars = request.Messages.GetRawText().Length + SystemChars(request.System, request.SystemBlocks) + (request.Tools?.GetRawText().Length ?? 0)
            + (request.OutputSchema?.GetRawText().Length ?? 0);
        return Cost(route, (int)Math.Min(int.MaxValue, chars / perToken + 1), request.MaxTokens);
    }

    /// <summary>The length of a call's system prompt: its blocks as JSON when it gives them, else its text.</summary>
    internal static int SystemChars(string? system, JsonElement? blocks) => Given(blocks) is { } given ? given.GetRawText().Length : system?.Length ?? 0;

    /// <summary>A JSON value the call gave, or null for JSON's null or none.</summary>
    private static JsonElement? Given(JsonElement? value) => value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } given ? given : null;

    private static decimal Cost(AiRoute route, int input, int output)
    {
        AiPrice price = route.Connection.PriceOf(route.Model);
        return Math.Round((input * price.Input + output * price.Output) / 1_000_000m, 6);
    }

    private async Task<AiReply> FinishAsync(AiCall call, AiReservation reservation, JsonElement message, int input, int output, int durationMs, CancellationToken ct)
    {
        decimal cost = Cost(call.Route, input, output);
        await ledger.SettleAsync(reservation, call, input, output, cost, durationMs, ct);
        await KeepPromptAsync(call, reservation.UsageId, message);
        return new AiReply(message, call.Route.Model, new AiUsage(input, output, cost)) { UsageId = reservation.UsageId, Connection = call.Route.Connection.Name };
    }

    private async Task KeepPromptAsync(AiCall call, Guid usageId, JsonElement reply)
    {
        try
        {
            await aiDb.RunAsync(async db =>
            {
                if (await db.AiSites.AsNoTracking().AnyAsync(s => s.Id == AiSite.TheId && s.KeepPrompts))
                {
                    string? system = Given(call.Request.SystemBlocks) is { } blocks ? blocks.GetRawText() : call.Request.System;
                    db.AiPrompts.Add(new AiPromptRecord(usageId, system, call.Request.Messages.GetRawText(), reply.GetRawText()));
                    await db.SaveChangesAsync();
                }
            }, requiresNew: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Keeping the prompt of call {UsageId} failed.", usageId);
        }
    }

    private static Exception Refusal(Exception ex, AiCall call, CancellationToken ct) => ex switch
    {
        OperationCanceledException when ct.IsCancellationRequested => ex,
        AiRefusedException => ex,
        OperationCanceledException => new AiRefusedException(AiRefusalCodes.Provider, $"{call.Route.Connection.Name} took too long to answer.", inner: ex),
        _ => new AiRefusedException(AiRefusalCodes.Provider, $"{call.Route.Connection.Name} failed: {ex.Message}", inner: ex),
    };
}
