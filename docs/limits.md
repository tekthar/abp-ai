# Limits

A limit is an amount of something a call counts (its measure) over a period: a `minute` from its start, a `day`, a
`week` from Monday, or a `month` from its first, in UTC. It is set on the site or on a scope.

| Measure | What it counts |
|---|---|
| `cost` | what calls cost, in the connections' price units (the default) |
| `tokens` | tokens in and out |
| `requests` | calls |

| Kind | Whose calls it counts |
|---|---|
| `total` | every call in the scope and the scopes under it (on the site, every call) |
| `person` | each person's calls there, by default, with no row per person |
| `group` | each member's calls, in place of `person`, for a group's members (the most generous of their groups) |
| `account` | one named account's calls: an agent's, or a person's in place of `person` |

So a site may hold 100 a month in cost, 2 million tokens a day for Finance, and 30 calls a minute for each person, each
a limit of its own: a limit of one measure never stands in for another, and the person's one limit (below) is chosen in
each measure and period apart.

A call must fit all that apply:

- every `total` on its scope, each scope above it, and the site;
- at each of those levels, its person's one limit: their `account` limit, else their groups', else `person`;
- when an account calls for someone (an agent's run), that account's own `account` limits too.

The person is `AiRequest.OnBehalfOf` when it's set (the person who started an agent's run), else `AccountId`. Their
spend counts every call by them or for them.

```csharp
await admin.SetLimitAsync(actor, financeId, new AiLimitInput(AiLimitKinds.Total, AiPeriods.Day, 2_000_000) { Measure = AiLimitMeasures.Tokens }, ct);
await admin.SetLimitAsync(actor, null, new AiLimitInput(AiLimitKinds.Person, AiPeriods.Minute, 30) { Measure = AiLimitMeasures.Requests }, ct);
```

A limit with no measure counts cost, so a host that set limits before measures keeps them as they were.

## The most tokens a reply may have

A `call-tokens` limit has no period and counts no spending: it is the most tokens one reply may have. Set it on the
site, on a scope (for its calls and those of the scopes under it), or for an account (with `AccountId`, on the site or
a scope): it applies to the account that calls and to the person it calls for. Before a call is estimated, the
gateway cuts its `MaxTokens` to the lowest that applies; the provider, the estimate and the demo model's responders all
see the cut. Its amount is a whole number, at least one.

```csharp
await admin.SetLimitAsync(actor, null, new AiLimitInput(AiLimitKinds.CallTokens, "", 4000), ct);
await admin.SetLimitAsync(actor, null, new AiLimitInput(AiLimitKinds.CallTokens, "", 1000, AccountId: agentId), ct);
```

## The ledger

Before a call leaves, its estimate is reserved under one lock, so calls at the same time can't go over a limit
together. The estimate is its text's tokens in (characters over `CharsPerToken`, system blocks counted as they're
sent) and all of its `MaxTokens` out: in cost at its model's prices, in tokens as they are, and one call. After it
answers, the record is settled to what it took: its tokens in, written to the provider's cache, read from it, and
out, at its model's prices for each. Token limits count every one of them. A provider's failure before it answered
costs nothing and isn't counted, nor is a refused call; a stream that failed after it began costs what it had used,
which counts in cost and tokens (not as a call). A reservation no one settles (the server stopped) stops holding after
`ReservationTimeout`.

A minute is the minute on the clock, not the last sixty seconds: a limit of 30 calls a minute lets 30 through in each
minute, starting again at the next.

## Refusals

`AiRefusedException` with code `over_limit`, and `Limit`: its kind (`scope`, `person`, `account`), where, whose, the
period, its measure, the amount and what's spent (in the measure's units), and `FreesAt`, when it frees, so a chat can
tell the person ("Your per-minute AI call limit on the site is reached (30 of 30 calls). It frees at 10:42:00 UTC.").
When more than one is reached, the one that frees last is named.

Every refusal before a call leaves (a limit, no model, the data rules) is kept on the ledger as `refused`, at no cost,
with its code and words, so usage counts refusals too. A call that isn't one the gateway can make (no alias, no
messages, `MaxTokens` not above 0) is refused with `invalid`, and isn't kept.

## Notices

When a call takes a limit from below `NoticeAt` (80%) to at or above it, `IAiNotices.LimitNearingAsync` hears once, in
the limit's own measure. When a limit is reached (a call takes it to its amount, or one is refused by it),
`IAiNotices.LimitReachedAsync` hears, once in each of its periods however many calls it refuses. A limit over a minute
is a rate that frees within it, so no one is told of it.

## An agent's own limits

An agent's account may have limits of its own on the site, which count its calls everywhere, inside the site's and
the scopes'. Its admins set them, as the host says who they are (`IAiRights.AdministersAccountAsync`), and so do the
site's admins: `AiAdminService.ListAccountLimitsAsync(actor, accountId)`, `SetAccountLimitAsync(actor, accountId,
input)` (`account` over a period, or `call-tokens`) and `DeleteLimitAsync`.
