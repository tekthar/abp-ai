# Limits

A limit is an amount (in the connections' price units) over a period: a `day`, a `week` from Monday, or a `month` from
its first, in UTC. It is set on the site or on a scope.

| Kind | What it counts |
|---|---|
| `total` | every call in the scope and the scopes under it (on the site, every call) |
| `person` | each person's calls there, by default, with no row per person |
| `group` | each member's calls, in place of `person`, for a group's members (the most generous of their groups) |
| `account` | one named account's calls: an agent's, or a person's in place of `person` |

A call must fit all that apply:

- every `total` on its scope, each scope above it, and the site;
- at each of those levels, its person's one limit: their `account` limit, else their groups', else `person`;
- when an account calls for someone (an agent's run), that account's own `account` limits too.

The person is `AiRequest.OnBehalfOf` when it's set (the person who started an agent's run), else `AccountId`. Their
spend counts every call by them or for them.

## The ledger

Before a call leaves, its estimate (its text's tokens in, all of its `MaxTokens` out) is reserved under one lock, so
calls at the same time can't go over a limit together. After it answers, the record is settled to what it cost. A
provider's failure costs nothing. A reservation no one settles (the server stopped) stops holding after
`ReservationTimeout`.

## Refusals

`AiRefusedException` with code `over_limit`, and `Limit`: its kind (`scope`, `person`, `account`), where, whose, the
period, the amount, what's spent, and `FreesAt`, when it frees, so a chat can tell the person. When more than one is
reached, the one that frees last is named.

## Notices

When a call takes a limit from below `NoticeAt` (80%) to at or above it, `IAiNotices.LimitNearingAsync` hears once.
