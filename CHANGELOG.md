# Changelog

All notable changes to AbpAi are written here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the packages follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html): before 1.0, a minor version may change the API.

## [Unreleased]

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
