# Changelog

All notable changes to AbpAi are written here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the packages follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html): before 1.0, a minor version may change the API.

## [Unreleased]

### Added

- **Embeddings:** `IAi.EmbedAsync(AiEmbedRequest)` (a default interface method, so a host's own `IAi` still builds):
  vectors for texts, one each, from a model an alias leads to, with the data rules, the limits, the ledger and the
  connection's total timeout as for any call (`AiEmbedding`, `AiEmbedInputTypes`). Models say they make them
  (`AiModelSettings.Embeds`; one found on a connection does when its id says `embed`; one with no settings takes
  everything, as before), and the router chooses for it (`AiNeeds.Embedding`, `AiChoice.Embeds`). Providers:
  `IAiProvider.CanEmbed` and `EmbedAsync` (default: can't); the OpenAI-compatible one calls `{endpoint}/embeddings`
  (OpenAI, Azure OpenAI, Ollama, vLLM, Voyage AI; `dimensions`, and `input_type` for a connection whose `input-type`
  setting is `send`); the demo model gives vectors that are the same for the same text everywhere and near for texts
  that share words (`DemoProvider.Vector`), so a search by meaning runs in tests. For an agent's memory (graphiti-net
  through AbpAgent).

- **Live chats:** `AiAgentModels.StreamAsync`, for AbpAgent 0.3.0's `IAgentModels.StreamAsync`: the Messages API's
  events as they come, then the whole reply; a refusal before the first event is thrown, and a failure after it (or a
  model that declines) is the Messages API's `error` event with the gateway's code as its type. AbpAi.Agents takes
  AbpAgent 0.3.1.
- An embedding call that asks for a size (`Dimensions`) gets vectors of that size, or is refused (`provider`).

### Fixed

- `AiAgentModels` hands AbpAgent every token a turn sent: in, written to the provider's cache and read from it. A
  run's token total and AbpAgent's token limits undercounted cached turns (the cost was right).

## [0.4.0] - 2026-11-12

What a host needs to run AbpAi in front of people: each connection's timeouts, a test that says why a connection
fails, an export of calls for support, and everything the first products to move onto 0.3.0 found. A host on 0.3.0
that changes nothing keeps working once its migration adds 0.4.0's columns and table (`dotnet ef migrations add
AbpAi040`), with the changes below.

### Added

- **Timeouts per connection (AI6):** an idle timeout (the longest wait for the next part of a streamed reply, its
  first included; site default `AbpAi:IdleTimeout`, a minute) and a total timeout (site default `ProviderTimeout`, 10
  minutes), on `AiConnectionInput`/`AiConnectionInfo` (`IdleTimeoutSeconds`, `TotalTimeoutSeconds`; 0 goes back to
  the site's) and `AiConnectionDescriptor` (`IdleTimeout`, `TotalTimeout`) for connections a host keeps. A call that
  runs out is refused with the new code `timeout`, and `AiRefusedException.TimedOut` says which (`AiTimeouts.Idle`,
  `Total`). `AiTimeoutsOf`.
- **A connection's test (AI7):** `AiAdminService.TestConnectionAsync(actor, scopeId, connectionId, model)`, one small
  call straight to the provider: `AiConnectionTest` (ok and the time taken, or the provider's status and its own
  reason, never the key). `AiConnectionInput.Test` tests on save, into `AiConnectionInfo.Test`.
- **An export of calls for support (AI8):** `AiExportService` (`CreateAsync`, `ListAsync`, `DownloadAsync`,
  `DeleteAsync`): the site's owner only (`IAiRights.IsSiteOwnerAsync`, by default no one), each one told to the audit
  log first (`IAiAudit`, `AiAuditActions`; the default writes to the log), JSON lines gzipped, prompts and replies
  only where the site kept them, deleted after `AbpAi:ExportDays` (7). Table `exports`; `AiExport`.
- **Prompt caching:** `AiRequest.Cache` (and `AiProviderCall.Cache`): Anthropic marks the last system block and the
  last message's last block, unless the call marked its own. Cached tokens are read from both built-in providers
  (Anthropic's `cache_creation_input_tokens` and `cache_read_input_tokens`; OpenAI's `cached_tokens`, taken out of its
  prompt tokens) into `AiProviderReply`, `AiUsage`, the ledger (`CacheWriteTokens`, `CacheReadTokens`) and usage
  reports, and priced (`AiPrice.CacheWrite`, `CacheRead`, by default 1.25 times and a tenth of the input price;
  `CacheWritePrice`, `CacheReadPrice` on a connection). Token limits count every token.
- **Retries:** the built-in providers try again on 429, 500, 502, 503, 504 and 529, and when the provider can't be
  reached, after its `retry-after` (at most 30 seconds) or a short wait that doubles: `AbpAi:ProviderRetries` (2),
  `AiProviderCall.Retries`, never once a stream has begun. `ProviderHttp` is public, for a host's own providers.
- **Refusals on the ledger:** a call refused before it leaves (a limit, no model, the data rules) is kept as
  `AiUsageStates.Refused`, at no cost, with its code (`AiUsageRecord.Code`, also set on failed calls) and words.
  Usage rows count `Failed` and `Refused` beside `Calls`; `AiUsageService.RecentAsync` and `MineRecentAsync` list the
  calls themselves, filtered by state (`AiUsageQuery.States`); `AiCallRow`.
- **A limit reached:** `IAiNotices.LimitReachedAsync` (a default interface method that does nothing), told once in
  each of a limit's periods, when a call takes it to its amount or it refuses one. `AiLimit.ReachedNoticeFor`.
- **An agent's own limits:** `AiAdminService.ListAccountLimitsAsync`, `SetAccountLimitAsync` (`account` or
  `call-tokens`, on the site) and `DeleteLimitAsync`, for the site's admins and the account's
  (`IAiRights.AdministersAccountAsync`, by default no one).
- **Agents:** `AiAgentCallInfo.Cache` (on by default), `MaxTokens` (a floor on each turn's) and `ProviderOptions`; a
  reply whose `stop_reason` is `refusal` ends the run with the code `refusal` (`AiAgentModels.RefusalStop`).
- `AiConnectionDescriptor.Type` and `AiChoice.ConnectionType`: the host's own name for a connection's type.
- `AiOptions.CapabilitiesDecide` (true): false lets a host whose models' flags are only for people to read have
  models chosen by alias, access and being on.
- `AiModel.IgnoreAi()`: leaves the gateway's tables out of a model, for a host's tests on SQLite or in memory.
- `AiOptions.DemoPacing`; `DemoProvider.Events` is public. `AiProviderException.Reason`.

### Changed

- A call that can't be made as it's asked (no alias, no messages, `MaxTokens` not above 0, system blocks or provider
  options of the wrong shape) is refused with `AiRefusalCodes.Invalid` (`AiRefusedException`) rather than throwing
  `ArgumentException`.
- A streamed call is bounded by its total timeout too; before, only a call that wasn't streamed was. A stream longer
  than 10 minutes needs its connection's `TotalTimeoutSeconds` raised.
- A stream that fails after it began (it timed out, or the provider broke off) costs what it had used, as one the
  caller stopped reading does; the limits count it in cost and tokens, not as a call. A call that fails before its
  first event still costs nothing. `AiLedger.FailAsync` takes what was used.
- A provider's error says the provider's own reason (its `error.message`) rather than the start of its body, with the
  key and anything shaped like one read `[key]`.
- The demo model's reply names the model the call went to and carries its usage; its stream comes in small pieces,
  thinking, signatures, citations and a tool's input included.
- The connections' key is named `PK_ai_connections` (the 0.4.0 migration renames it; a host that already named it so
  has nothing to do).
- The usage CSV has two more columns at its end, `cache_write_tokens` and `cache_read_tokens`.

### Fixed

- The OpenAI-compatible provider sends `search_result` blocks, in a user's turn and in a tool's result, as text (their
  source, title and text) rather than dropping them.
- A stream's message keeps its citations (`citations_delta`).
- Every gap 0.3.0's changelog listed is closed.


## [0.3.0] - 2026-11-12

What a product with a gateway of its own needs to move onto AbpAi, the product the gateway came from first
(docs/moving-onto-abp-ai.md). A host on 0.2.0 that changes nothing keeps working as it did, once its migration adds
0.3.0's columns (their defaults keep what's there as it was).

### Added

- Requests: `AiRequest.SystemBlocks` (the Messages API's system blocks, with `cache_control`; they win over `System`) and
  `AiRequest.ProviderOptions` (a JSON object of keys the gateway doesn't set itself, such as `thinking` and
  `temperature`). `AiProviderCall` carries both and the whole `AiRequest` (`Request`), so demo responders and a host's
  providers see the purpose, account, scope, `About` and tools; `AiProviderCall.SystemText`, `AiMessages.SystemText`.
  Anthropic sends the blocks as they are and merges the options; the OpenAI-compatible provider joins the blocks' text
  into its system message and takes the options in its own shape. The estimate counts the blocks.
- Models with what they can do: `AiConnection.ModelSettings`, by the provider's model id, of `AiModelSettings` (`Name`,
  `Enabled`, `Tools`, `Images`, `StructuredOutput`, `ContextTokens`, `PriceSetAt`). A call names a model by its name or
  id; the router skips models that are off and chooses for tools, images (`AiMessages.HasImages`) and a schema
  (`AiNeeds`). `AiChoice.ModelName`, `Tools`, `Images`; `AiConnectionInput.ModelSettings` and
  `AiConnectionInfo.ModelSettings`.
- Model discovery: `IAiProvider.ListModelsAsync` (by default null, "can't list"), implemented by Anthropic
  (`GET /v1/models`), OpenAI-compatible (`GET {endpoint}/models`) and the demo model (`DemoProvider.ModelId`, `demo`);
  `AiAdminService.DiscoverModelsAsync` (adds what's found, each off) and `ProviderModelsAsync` (asks, for any source).
- Which site models a scope may use: `AiSite.ScopeAccess` (`AiScopeAccessModes.Open`, the default, or `Listed`), the
  entity `AiScopeAccess` (table `ai.scope_access`: `AllSiteModels`, `SiteModels` of `AiModelRef`, `MayConnect`),
  inherited down the scope chain; `AiAdminService.GetScopeAccessAsync`, `ListScopeAccessAsync`, `SetScopeAccessAsync`,
  `DeleteScopeAccessAsync` (site admins only); `AiSiteSettings.ScopeAccess`; `IAiDbContext.AiScopeAccess` (with a
  default, so a host's context needn't name it).
- Limits by what they count: `AiLimit.Measure` (`AiLimitMeasures`: `cost`, the default, `tokens`, `requests`), the period
  `minute`, and the kind `call-tokens` (no period: the most tokens one reply may have, on the site, a scope or an
  account; a call's `MaxTokens` is cut to the lowest before it's estimated, `AiLedger.CallTokensAsync`).
  `AiLimitRef.Measure`, `AiLimitInput.Measure`, `AiLimitInfo.Measure`, `AiCall.EstimatedTokens`,
  `AiUsageRecord.ReservedTokens`. Notices at 80% in every measure (not of a minute's rate).
- Connections the host keeps: `IAiConnectionSource` (`ListAsync` for a call's scope chain, `KeyAsync` only as a call
  goes out) and `AiConnectionDescriptor`; the default, `StoredAiConnections`, reads AbpAi's `connections` table. With a
  host's source, `AiAdminService.ListConnectionsAsync` lists the host's and creating, changing, deleting or discovering
  a connection is refused with `AiErrorCodes.HostConnections` (409 in the sample). `AiAdminService.KeepsConnections`.
- Provider kinds of a host's own may carry a prefix (`acme:bedrock`).
- The sample makes its tables with migrations (`AbpAi020`, `AbpAi030`), and a test migrates 0.2.0's tables with a site's
  rows in them. `scripts/check-names.sh`, in CI.

### Changed

- `AiRoute.Connection` is an `AiConnectionDescriptor` (was the `AiConnection` entity), and `AiRoute` says the model's name
  and what it can do. `AiRouter.ChooseAsync` takes `AiNeeds`; the overload with `bool structured` stays.
- `AiPrice` lives in `AbpAi.Contracts` (forwarded from `AbpAi`, so code built against 0.2.0 still finds it).
- `AiPeriods.All` holds `minute` and `AiLimitKinds.All` holds `call-tokens`; the limits' unique index holds the measure.
- A connection's key is read through the source after a call's reservation, so a refused call never decrypts it.
- An alias's target that names a model by its name is kept by its id.
- `AbpAiModule.Version` is `0.3.0` (0.2.0 said `0.1.0`).

### Fixed

- A usage row cuts a long purpose, alias, connection, provider, model or data class to its column, so the call is
  recorded with the name shortened instead of failing with a database error.

### Known gaps

Found moving the first product onto 0.3.0; each is worked around by that host and is for a later version:

- The demo provider streams text only in whole pieces: no citations, tool input in one delta, no thinking deltas, no
  pacing, and its model id is always `demo`. A host that needs the Messages API's full stream registers its own `demo`
  provider after AbpAi's (the last provider of a kind wins).
- A call refused over a limit keeps no usage row, and `AiUsageStates` has no refused state; `AiUsageService` has no
  refused or failed counts and no recent-calls list.
- `IAiNotices` is told at 80% only, not when a limit is reached.
- Model capabilities always decide which models may answer (`AiModelSettings.Images` defaults to false). A host whose
  own flags are advisory says every model can.
- `ConfigureAi` names the connections' key `PK_connections`, which clashes with a host's own `connections` table; such
  a host names it in its model (`HasName("PK_ai_connections")`).
- A scope's admins can't set an account's own limit through `AiAdminService`; a host where they may writes the table.
- `AiChoice.Provider` is the provider's kind, not the host's own name for the connection's type.
- An invalid request (no messages, `MaxTokens` not above 0) throws `ArgumentException` rather than refusing.
- The OpenAI-compatible provider drops `search_result` blocks from user content, and a provider error shows the raw
  response body rather than its `error.message`.

## [0.2.0] - 2026-11-10

The first release, at the same version as AbpGuard and AbpAgent.

### Added

- `AbpAi.Contracts`: `IAi` (`CompleteAsync`, `StreamAsync`, `ResolveAsync`), `AiRequest`, `AiReply`, `AiStreamEvent`,
  `AiChoice`, `AiRefusedException` (`no_model`, `over_limit`, `provider`, `data_not_allowed`) with `AiLimitRef`,
  `IAiProvider`, `IAiDemoResponder`, `AiMessages`, `AiDataClasses`, and the host's seams `IAiScopes`, `IAiRights`,
  `IAiAccounts`, `IAiNotices`.
- `AbpAi`: the gateway (`AiService`), aliases through the host's scope chain (`AiRouter`), the usage ledger with
  limits per scope, per person, per group and per account (`AiLedger`), data rules (`AiDataRules`), the admin API
  (`AiAdminService`), usage queries and CSV export (`AiUsageService`), the demo model's script (`AiScript`),
  providers for Anthropic, OpenAI-compatible APIs and the demo model, and housekeeping. Tables in schema `ai`.
- `AbpAi.Agents`: AbpAgent's `IAgentModels` on `IAi` (`AiAgentModels`, `AbpAiAgentsModule`, `IAiAgentCalls`), on
  `AbpAgent.Contracts` 0.2.0.
- A sample host (an organization's departments over projects) and its tests on PostgreSQL.
