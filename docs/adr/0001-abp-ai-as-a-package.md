# 0001. AbpAi as a package

- **Status:** Proposed (from "AbpAi: what Insight needs", 10 Nov 2026)
- **Date:** 2026-11-10
- **Revisit at:** AbpAi 1.0, or when the product the gateway came from runs on it in production

## Context

The model gateway is part of one product today: its SDK's `IAi` and its platform's `AiService` (that product's ADR
0023). AbpAgent leaves the model to the host (`IAgentModels`), so every host that wants agents writes its own gateway,
or a thin wrapper over that product's. Insight would be the next to write one.

Insight also needs what the gateway doesn't have: rules for which data may go to which provider. Its answers are built
from the organization's data, and some of it must never leave for a public model. Its places are model sets and org
units, not projects; and it opens Ask to the whole organization, so one person must not spend everyone's budget.

## Decision

**AbpAi is the gateway, as a package with neutral names: three packages, tables in schema `ai`, and the host's scopes
in place of projects.** It is built from the requirements in this repository; moving the product the gateway came from
onto it is a step of its own ([moving onto AbpAi](../moving-onto-abp-ai.md)), since its code wasn't reachable when this
was written.

### What is in it (AI0)

- `AbpAi.Contracts`, plain .NET: `IAi` (`CompleteAsync`, `StreamAsync`, `ResolveAsync`), `AiRequest`, `AiReply`,
  `AiStreamEvent`, `AiChoice` (no key), `AiRefusedException` with `no_model`, `over_limit`, `provider` and
  `data_not_allowed`, `IAiProvider`, `IAiDemoResponder`, and the host's seams.
- `AbpAi`: `AiService`, `AiRouter` (aliases), `AiLedger` (reserve, settle, notices), `AiDataRules`, `AiAdminService`,
  `AiUsageService`, `AiScript`, the providers (Anthropic, OpenAI-compatible, demo), housekeeping, and the model.
- `AbpAi.Agents`: `AiAgentModels`, AbpAgent's `IAgentModels` on `IAi`.
- No product name anywhere. The ledger's lock is a Postgres advisory lock whose key spells `AI LEDGR`; an architecture
  test holds that no type is named for a product and the contracts reference nothing but .NET.

### The seams

| Seam | What it answers | Default |
|---|---|---|
| `IAiScopes` | a scope's parent; scopes' keys and titles | one level: every scope's parent is the site |
| `IAiRights` | is the actor a site admin; does the actor administer a scope (and so those under it) | no one |
| `IAiAccounts` | an account's groups, for group limits | none |
| `IAiNotices` | a limit reached its notice mark | the log |
| `IAiProvider` | a kind of provider | Anthropic, OpenAI-compatible, demo |
| `IAiDemoResponder` | the demo model's reply | `AiScript` (empty), then "this is the demo model" |

### Rules

- **Scopes (AI1).** A call names its scope. Its chain is the scope and every scope above it (from `IAiScopes`). An
  alias is the nearest one set on the chain, else the site's; a connection set on a scope serves the scopes under it.
  A call counts against every total on its chain and the site's. The ledger keeps each call's path, so a department's
  total is one query however its projects nest.
- **Per person (AI2).** At each level (the site and each scope on the chain) and each period, the person (the one a
  call is for, else its account) has one limit: their named account limit, else the most generous of their groups',
  else the level's per-person default. An account that calls for someone (an agent) also meets its own named limit.
  A call must fit every one of them and every total. The refusal names the limit (`AiLimitRef`) and when it frees.
- **Data (AI3).** The site defines its classes, in order. A connection lists what it may receive (an explicit list,
  so an admin sees exactly what may leave) and labels for where it runs. A call's class is its own when the site
  defines it, else the highest. Of an alias's targets, the first whose connection may receive the class is used; when
  none may, the call is refused with `data_not_allowed` and nothing is sent. A scope's own connection may list only
  what the site lets scopes' connections receive (by default `public` and `internal`), and the router narrows it again
  at each call, so narrowing the site's setting takes effect at once. Prompts are kept only when the site turns that
  on, and deleted after its period, or all at once when it turns that off.
- **Reservations.** A call reserves its estimate (its text's tokens in, all its `MaxTokens` out, at the connection's
  prices) under the lock, then settles to what it cost. A reservation no one settles stops holding after
  `ReservationTimeout` and is marked abandoned. A provider's failure costs nothing; a stream the caller stops reading
  costs what it used.
- **Notices.** When a call takes a limit from below its mark (80%) to at or above it, `IAiNotices` hears once.

### Open questions, answered

| Question | Answer |
|---|---|
| Does the product the gateway came from move onto AbpAi, or keep its own and share only the contracts? | **It moves onto AbpAi.** Two gateways means two sets of limits and data rules, and the data rules are the point. Its `IAi` callers keep their calls; its projects become scopes through an `IAiScopes` that says a project has no parent; its tables move to schema `ai` once. See [moving onto AbpAi](../moving-onto-abp-ai.md). |
| AI1: should scope parents come from the host, or from AbpGuard's org tree when it's installed? | **From the host, through `IAiScopes`.** AbpAi doesn't depend on AbpGuard. A host with AbpGuard writes an `IAiScopes` over its org tree (a small adapter; a package `AbpAi.Guard` can ship it when a second host needs it). Insight's model sets name their org unit as parent. |
| AI3: a fixed set of data classes in the package, or classes each site defines? | **Each site defines them (`AbpAi:DataClasses`), in order, with the package's five as the default.** Order matters only for what an unstated or unknown class counts as (the highest). A connection's list is explicit, not "up to". |
| AI0: does the `IAgentModels` bridge live with AbpAi or with AbpAgent? | **With AbpAi, as `AbpAi.Agents`.** AbpAgent stays free of any gateway; AbpAi already knows the Messages API's shape both speak. It is built against AbpAgent's contracts from source until they are on nuget.org. |
| Embeddings now, or when needed? | **When needed.** `IAi` can take an `EmbedAsync` by addition (a default that refuses with `no_model`), and `IAiProvider` likewise; the ledger, limits and data rules apply unchanged. Adding it before Insight's first use would guess at its shape. |

### Not now

- **HTTP controllers in the package.** The admin and usage services are the API; a host maps them to its own routes
  and shapes, as the sample does. A package of controllers can come when a second host wants the same routes.
- **Token counting by the provider, and cache prices.** The estimate is by characters; the settled cost is the
  provider's count. A call may mark what a provider may cache (system blocks with `cache_control`, from 0.3.0), and
  the gateway passes it on, but prices cached tokens as any other.
- **Per-label rules** ("only `on-premises` for `sensitive`"). Labels are for people to read today; the class list is
  the rule.

## Steps

| Step | What | Status |
|---|---|---|
| AI0 | The three packages, neutral names, built-in providers, the admin API, an architecture test | Done here |
| AI3 | Data classes, each connection's list and labels, `data_not_allowed` and fallbacks, fail closed, prompts kept only when on | Done here |
| AI1 | Scopes the host defines, nesting, aliases and connections inherited, totals up the chain | Done here |
| AI2 | Per person: a default, account and group overrides, alongside totals; the refusal names the limit and when it frees | Done here |
| AI5 | `AiScript` and the demo model in the package; a question set run through `AbpAi.Agents` in CI | Done here; AbpAgent's own eval harness (A8) to use it |
| AI4 | Usage by scope, person, purpose, model; export; a person's own | Done here |
| AI6 | What that product's move needs of the gateway (0.3.0): system blocks and provider options; models' settings and discovery; which site models a scope may use; limits on tokens and calls, per minute, and per reply; connections the host keeps (`IAiConnectionSource`) | Done here |
| Move | The product the gateway came from runs on AbpAi with no change to its calls, aliases, limits or usage history | To do, in that product |

## Consequences

- Every host that calls models, and every agent, gets one set of limits and data rules.
- A call that states no data class goes only where everything may go. Hosts must state classes to use public models
  for anything, which is the point, and means existing callers moving over must add them (or be given an alias whose
  targets allow the highest class).
- A call's reservation counts all of its `MaxTokens`, so a small limit refuses a call that asks for a long reply
  even when its reply would have been short.
