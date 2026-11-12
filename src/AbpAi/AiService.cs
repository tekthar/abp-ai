using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;

namespace AbpAi;

/// <summary>A connection's two timeouts: the longest wait for the next part of a reply, and the longest a whole call may take.</summary>
public sealed record AiTimeoutsOf(TimeSpan Idle, TimeSpan Total)
{
    /// <summary>A connection's own, else the site's (<see cref="AiOptions.IdleTimeout"/>, <see cref="AiOptions.ProviderTimeout"/>).</summary>
    public static AiTimeoutsOf For(AiConnectionDescriptor connection, AiOptions options) =>
        new(connection.IdleTimeout is { } idle && idle > TimeSpan.Zero ? idle : options.IdleTimeout,
            connection.TotalTimeout is { } total && total > TimeSpan.Zero ? total : options.ProviderTimeout);

    /// <summary>A timeout as people read it: seconds under two minutes, else minutes.</summary>
    public static string Words(TimeSpan span) => span < TimeSpan.FromMinutes(2)
        ? $"{Math.Round(span.TotalSeconds):0} second{(Math.Round(span.TotalSeconds) == 1 ? "" : "s")}"
        : $"{span.TotalMinutes:0.#} minutes";
}

/// <summary>
/// The gateway (<see cref="IAi"/>). Each call: its <c>MaxTokens</c> is cut to the lowest per-call limit that applies;
/// the alias leads to a model through the call's scope chain; the data rules choose the first target that may receive
/// the call's class (or refuse, before anything leaves); the ledger reserves the estimate against every limit that
/// applies; the connection's key is asked of its source; the provider answers within the connection's timeouts; the
/// record is settled with what it cost; and the prompt is kept only when the site keeps prompts. A call refused before
/// it leaves (no model, the data rules, a limit) is kept on the ledger as refused, at no cost.
/// </summary>
public sealed class AiService(AiRouter router, AiScopeChains chains, AiLedger ledger, AiDb aiDb, IEnumerable<IAiProvider> providers, IAiConnectionSource connections,
    IOptions<AiOptions> options, ILogger<AiService> logger) : IAi, ITransientDependency
{
    public async Task<AiChoice> ResolveAsync(string alias, Guid? scopeId = null, string? dataClass = null, CancellationToken cancellationToken = default)
    {
        AiScopeChain chain = await chains.OfAsync(scopeId, cancellationToken);
        AiRoute route = await router.ChooseAsync(alias, chain, router.ClassOf(dataClass), AiNeeds.None, cancellationToken);
        return route.ToChoice();
    }

    public async Task<AiReply> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        (AiCall call, IAiProvider provider) = await PrepareAsync(request, streamed: false, cancellationToken);
        AiReservation reservation = await ReserveAsync(call, cancellationToken);
        AiTimeoutsOf timeouts = AiTimeoutsOf.For(call.Route.Connection, options.Value);
        Stopwatch watch = Stopwatch.StartNew();
        AiProviderReply answer;
        using CancellationTokenSource total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(timeouts.Total);
        try
        {
            AiProviderCall providerCall = await ProviderCallAsync(call, total.Token);
            answer = await provider.CompleteAsync(providerCall, total.Token);
        }
        catch (Exception ex)
        {
            Exception refusal = Refusal(ex, call, cancellationToken, total, null, timeouts);
            await ledger.FailAsync(reservation, refusal.Message, (int)watch.ElapsedMilliseconds, (refusal as AiRefusedException)?.Code);
            throw refusal;
        }

        return await FinishAsync(call, reservation, answer.Message, new AiSettlement(answer.InputTokens, answer.OutputTokens, answer.CacheWriteTokens, answer.CacheReadTokens),
            (int)watch.ElapsedMilliseconds, cancellationToken);
    }

    public async IAsyncEnumerable<AiStreamEvent> StreamAsync(AiRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        (AiCall call, IAiProvider provider) = await PrepareAsync(request, streamed: true, cancellationToken);
        AiReservation reservation = await ReserveAsync(call, cancellationToken);
        AiTimeoutsOf timeouts = AiTimeoutsOf.For(call.Route.Connection, options.Value);
        Stopwatch watch = Stopwatch.StartNew();

        // The total timeout bounds the whole call; the idle one each wait for the next event (the first included), and
        // is held while the caller reads one.
        CancellationTokenSource total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource idle = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
        Providers.AiMessageBuilder builder = new();
        IAsyncEnumerator<AiStreamEvent>? events = null;
        bool ended = false;
        try
        {
            total.CancelAfter(timeouts.Total);
            idle.CancelAfter(timeouts.Idle);
            AiProviderCall providerCall;
            try
            {
                providerCall = await ProviderCallAsync(call, idle.Token);
                events = provider.StreamAsync(providerCall, idle.Token).GetAsyncEnumerator(idle.Token);
            }
            catch (Exception ex)
            {
                ended = true;
                Exception refusal = Refusal(ex, call, cancellationToken, total, idle, timeouts);
                await ledger.FailAsync(reservation, refusal.Message, (int)watch.ElapsedMilliseconds, (refusal as AiRefusedException)?.Code);
                throw refusal;
            }

            while (true)
            {
                AiStreamEvent e;
                try
                {
                    idle.CancelAfter(timeouts.Idle);
                    if (!await events.MoveNextAsync())
                    {
                        break;
                    }

                    idle.CancelAfter(Timeout.InfiniteTimeSpan);
                    e = events.Current;
                    builder.Add(e);
                }
                catch (Exception ex)
                {
                    // A stream that failed after it began costs what it had used: the provider charged for it.
                    ended = true;
                    Exception refusal = Refusal(ex, call, cancellationToken, total, idle, timeouts);
                    AiSettlement used = new(builder.InputTokens, builder.OutputTokens, builder.CacheWriteTokens, builder.CacheReadTokens);
                    await ledger.FailAsync(reservation, refusal.Message, (int)watch.ElapsedMilliseconds, (refusal as AiRefusedException)?.Code, used, Cost(call.Route, used));
                    throw refusal;
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

            idle.Dispose();
            total.Dispose();
            if (!ended)
            {
                // The caller stopped reading: the call is over, and costs what it had used by then.
                AiSettlement used = new(builder.InputTokens, builder.OutputTokens, builder.CacheWriteTokens, builder.CacheReadTokens);
                await ledger.SettleAsync(reservation, call, used, Cost(call.Route, used), (int)watch.ElapsedMilliseconds, CancellationToken.None);
            }
        }

        AiReply reply = await FinishAsync(call, reservation, builder.Message,
            new AiSettlement(builder.InputTokens, builder.OutputTokens, builder.CacheWriteTokens, builder.CacheReadTokens), (int)watch.ElapsedMilliseconds, cancellationToken);
        yield return new AiStreamEvent(AiStreamEvent.Done, reply.Message) { Reply = reply };
    }

    private async Task<(AiCall Call, IAiProvider Provider)> PrepareAsync(AiRequest request, bool streamed, CancellationToken ct)
    {
        if (Invalid(request) is { } why)
        {
            throw new AiRefusedException(AiRefusalCodes.Invalid, why);
        }

        AiScopeChain chain = await chains.OfAsync(request.ScopeId, ct);
        if (await ledger.CallTokensAsync(request, chain, ct) is { } most && most < request.MaxTokens)
        {
            request = request with { MaxTokens = most };
        }

        string dataClass = router.ClassOf(request.DataClass);
        AiRoute route;
        try
        {
            route = await router.ChooseAsync(request.Alias, chain, dataClass, AiNeeds.Of(request), ct);
        }
        catch (AiRefusedException refused)
        {
            await ledger.RefusedAsync(request, chain, dataClass, null, streamed, refused, ct);
            throw;
        }

        (decimal cost, int tokens) = Estimate(request, route);
        AiCall call = new(request, chain, route, dataClass, cost, streamed) { EstimatedTokens = tokens };
        IAiProvider? provider = providers.LastOrDefault(p => string.Equals(p.Kind, route.Connection.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            AiRefusedException refused = new(AiRefusalCodes.NoModel, $"No provider \"{route.Connection.Provider}\" is installed for {route.Connection.Name}.");
            await ledger.RefusedAsync(request, chain, dataClass, route, streamed, refused, ct);
            throw refused;
        }

        return (call, provider);
    }

    /// <summary>Why a call can't be made as it's asked; null when it can.</summary>
    internal static string? Invalid(AiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Alias))
        {
            return "A call names an alias or a model.";
        }

        if (request.Messages.ValueKind != JsonValueKind.Array || request.Messages.GetArrayLength() == 0)
        {
            return "A call's messages are a JSON array of { role, content }, and not empty.";
        }

        if (request.MaxTokens <= 0)
        {
            return "A call's MaxTokens is more than nothing.";
        }

        if (request.SystemBlocks is { ValueKind: not (JsonValueKind.Array or JsonValueKind.String or JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            return "A call's SystemBlocks are a JSON array of the Messages API's blocks, or a string.";
        }

        if (request.ProviderOptions is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            return "A call's ProviderOptions are a JSON object.";
        }

        return null;
    }

    /// <summary>The call's estimate held on the ledger; a limit's refusal is kept there as refused, and its admins hear it's reached.</summary>
    private async Task<AiReservation> ReserveAsync(AiCall call, CancellationToken ct)
    {
        try
        {
            return await ledger.ReserveAsync(call, ct);
        }
        catch (AiRefusedException refused) when (refused.Code == AiRefusalCodes.OverLimit)
        {
            await ledger.RefusedAsync(call.Request, call.Chain, call.DataClass, call.Route, call.Streamed, refused, ct);
            throw;
        }
    }

    /// <summary>The call as its provider gets it, with the connection's key: asked of the source only now, as the call goes out.</summary>
    private async Task<AiProviderCall> ProviderCallAsync(AiCall call, CancellationToken ct)
    {
        AiRequest request = call.Request;
        AiConnectionDescriptor connection = call.Route.Connection;
        string? key = await connections.KeyAsync(connection.Id, ct);
        AiTarget target = new(connection.Id, connection.Name, connection.Endpoint, key, call.Route.Model, connection.Settings);
        return new AiProviderCall(target, request.Messages, request.System, request.Tools, request.MaxTokens, request.OutputSchema)
        {
            Purpose = request.Purpose,
            ScopeId = request.ScopeId,
            SystemBlocks = Given(request.SystemBlocks),
            ProviderOptions = Given(request.ProviderOptions),
            Request = request,
            Cache = request.Cache,
        };
    }

    /// <summary>A call's estimate before it leaves: its text's tokens in, and all its MaxTokens out; what they cost, and how many.</summary>
    private (decimal Cost, int Tokens) Estimate(AiRequest request, AiRoute route)
    {
        int perToken = Math.Max(1, options.Value.CharsPerToken);
        long chars = request.Messages.GetRawText().Length + SystemChars(request.System, request.SystemBlocks) + (request.Tools?.GetRawText().Length ?? 0)
            + (request.OutputSchema?.GetRawText().Length ?? 0);
        int input = (int)Math.Min(int.MaxValue, chars / perToken + 1);
        return (Cost(route, new AiSettlement(input, request.MaxTokens, 0, 0)), (int)Math.Min(int.MaxValue, (long)input + request.MaxTokens));
    }

    /// <summary>The length of a call's system prompt: its blocks as JSON when it gives them, else its text.</summary>
    internal static int SystemChars(string? system, JsonElement? blocks) => Given(blocks) is { } given ? given.GetRawText().Length : system?.Length ?? 0;

    /// <summary>A JSON value the call gave, or null for JSON's null or none.</summary>
    private static JsonElement? Given(JsonElement? value) => value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } given ? given : null;

    /// <summary>What a call's tokens cost at its model's prices: in, written to the cache, read from it, and out.</summary>
    internal static decimal Cost(AiRoute route, AiSettlement used)
    {
        AiPrice price = route.Connection.PriceOf(route.Model);
        return Math.Round((used.InputTokens * price.Input + used.CacheWriteTokens * price.CacheWriteOrDefault + used.CacheReadTokens * price.CacheReadOrDefault
            + used.OutputTokens * price.Output) / 1_000_000m, 6);
    }

    private async Task<AiReply> FinishAsync(AiCall call, AiReservation reservation, JsonElement message, AiSettlement used, int durationMs, CancellationToken ct)
    {
        decimal cost = Cost(call.Route, used);
        await ledger.SettleAsync(reservation, call, used, cost, durationMs, ct);
        await KeepPromptAsync(call, reservation.UsageId, message);
        return new AiReply(message, call.Route.Model, new AiUsage(used.InputTokens, used.OutputTokens, cost)
        {
            CacheWriteTokens = used.CacheWriteTokens,
            CacheReadTokens = used.CacheReadTokens,
        })
        {
            UsageId = reservation.UsageId,
            Connection = call.Route.Connection.Name,
        };
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

    /// <summary>
    /// What a failure is to the caller: the caller's own stop as it is; a timeout that ran out as a <c>timeout</c>
    /// refusal naming which; the provider's error as a <c>provider</c> refusal with its words.
    /// </summary>
    private static Exception Refusal(Exception ex, AiCall call, CancellationToken caller, CancellationTokenSource total, CancellationTokenSource? idle, AiTimeoutsOf timeouts)
    {
        string name = call.Route.Connection.Name;
        return ex switch
        {
            OperationCanceledException when caller.IsCancellationRequested => ex,
            AiRefusedException => ex,
            OperationCanceledException when total.IsCancellationRequested => new AiRefusedException(AiRefusalCodes.Timeout,
                $"{name} took longer than {AiTimeoutsOf.Words(timeouts.Total)} to answer (its total timeout).", inner: ex) { TimedOut = AiTimeouts.Total },
            OperationCanceledException when idle?.IsCancellationRequested == true => new AiRefusedException(AiRefusalCodes.Timeout,
                $"{name} sent nothing for {AiTimeoutsOf.Words(timeouts.Idle)} (its idle timeout).", inner: ex) { TimedOut = AiTimeouts.Idle },
            OperationCanceledException => new AiRefusedException(AiRefusalCodes.Provider, $"{name} took too long to answer.", inner: ex),
            _ => new AiRefusedException(AiRefusalCodes.Provider, $"{name} failed: {ex.Message}", inner: ex),
        };
    }
}
