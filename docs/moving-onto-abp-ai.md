# Moving a product onto AbpAi

For the product the gateway came from (its SDK's `IAi`, its platform's `AiService`, `AiAdmin` and providers), which
keeps its connections (a connections page of its own, with their secrets) and its rows for each connection's models
(`AiModel`: on or off, a name, tools, images, structured output, context, prices and when they were set). Those stay
its own, read through an `IAiConnectionSource`. Its aliases, project access, limits and usage move into AbpAi's tables.
Done when it runs on AbpAi with no change to its calls, aliases, limits or usage history.

1. **Calls.** Its SDK's `IAi` becomes a thin type over `AbpAi.IAi`, field for field:

   | Its request | AbpAi's `AiRequest` |
   |---|---|
   | `AccountId`, `Alias`, `Messages`, `Tools`, `MaxTokens`, `Purpose` | the same |
   | `ProjectId` | `ScopeId` |
   | `System` (a string, or the Messages API's blocks) | `SystemBlocks` (it takes either; it wins over `System`) |
   | `Options` (passed through to the provider) | `ProviderOptions` |
   | `OutputSchema` (a JSON object) | `OutputSchema` (as a `JsonElement`) |
   | `RecordId` | `About = new AiSubject("record", id)` |
   | (none) | `DataClass`: none, so the highest; see step 6 |

   Its reply is AbpAi's (message, model, usage). Its stream events are AbpAi's (`Type`, `Data` as JSON) without the last
   one (`AiStreamEvent.Done`), which carries the whole reply; a failure after the first event, which AbpAi throws as a
   refusal, becomes its `error` event. Its `ResolveAsync` answers null where AbpAi refuses with `no_model` or
   `data_not_allowed`; `AiChoice.ModelName` and `Tools` are there as it has them. Its refusal codes are AbpAi's
   (`no_model`, `over_limit`, `provider`, with `data_not_allowed` new); its limit's text comes from `AiLimitRef`.

2. **Scopes and rights.** An `IAiScopes` whose `ParentOfAsync` answers null for every project (one level, as today),
   and whose `GetManyAsync` names them for usage. An `IAiRights` over its abilities: the site's admins, and a project's.

3. **Connections and models stay.** An `IAiConnectionSource` reads them. `ListAsync` gives the site's connections and
   the projects' named, each with its model rows: `Models` their model ids, `ModelSettings` their name, on or off, tools,
   images, structured output, context and price date, `ModelPrices` their prices (a model with no price costs nothing,
   as today). Its connection types map to AbpAi's provider kinds (`openai-compatible` to `openai`, its demo model to
   `demo`); its address setting is `Endpoint`. `KeyAsync` gives the connection's key from its secrets, asked only as a
   call goes out. Its connections page keeps setting connections; its models page keeps its rows, and finds a
   connection's models with `AiAdminService.ProviderModelsAsync` (or the provider's `ListModelsAsync`). AbpAi's own
   connection admin refuses with `HostConnections`, as it should.

4. **Providers.** Its built-in providers are AbpAi's. A plugin's provider becomes an `AbpAi.IAiProvider` of its own
   kind (`{plugin}:{type}`, colon and all): `CompleteAsync`, `StreamAsync` and `ListModelsAsync`, with the connection's
   address, key, settings and model id in `AiProviderCall.Target` and the whole request in `AiProviderCall.Request`.

5. **Tables.** Its context maps AbpAi's (`ConfigureAi`), and one migration, under its startup lock, makes them and moves
   its rows: its aliases, project access, limits and usage (steps 6 to 8). Its connections and model rows aren't
   touched. Its old AI tables are dropped a release later. Check the columns against `AiModel.ConfigureAi` and write
   the mapping in the migration:

   | Its rows | AbpAi's |
   |---|---|
   | an alias (`ProjectId`, `Name`, `ModelId`) | `aliases`: `ScopeId` = `ProjectId`, the same `Name`, `Targets` = `[{ connectionId, model }]` of its model row (the connection, the model's id) |
   | a usage entry | `usage`: `ScopeId` = `ProjectId`, `ScopePath` = `ARRAY[ProjectId]` (empty for the site's), `AboutKind` = `record` and `AboutId` = `RecordId` when it has one, `Model` its model id, `Connection` and `Provider` its connection's name and kind, `Alias` its model's name, `DataClass` the site's highest, `Reserved` and `ReservedTokens` its estimates; `State`: `ok` is `done`, `failed` and `refused` are `failed` (they cost nothing), `reserved` is `abandoned` |

6. **Data classes.** Its calls state none, so they count as the highest: its source gives every connection every class
   (`public` … `sensitive`), and the site's `ScopeDataClasses` is every class too, so projects' own connections keep
   receiving what they do today. Then the product states classes where it knows them and the site narrows public
   connections.

7. **Project access.** The site's `ScopeAccess` is `listed` and `ScopesMayConnect` true. Each project's access row
   becomes its `scope_access` row: `AllSiteModels` = `AllModels`, `SiteModels` = its `ModelIds` as `(connection, model
   id)` of their rows, `MayConnect` = `OwnConnections`. A project with no row may use none of the site's models and no
   connection of its own, as today; the site's own calls use every site model.

8. **Limits.** Each of its limits keeps its amount, with its kind as AbpAi's measure: `cost`, `tokens`, `requests`, or
   the kind `call-tokens` (no period). The site's become `total` on the site, a project's `total` on its scope, an
   account's (an agent's) `account` on the site with that account. Its `minute` was the last sixty seconds; AbpAi's is
   the minute on the clock. A failed call counted as a request there and doesn't here. Its notices at 80% are
   `IAiNotices`; at 100%, the refusal says it.

9. **The demo model.** Its demo responders become `AbpAi.IAiDemoResponder`s and register as before: they get the call,
   with the whole request in `AiProviderCall.Request`, and answer the assistant message as a `JsonElement`.

10. **The ledger's lock.** Its old constant goes; AbpAi's is `AiLedger.LockKey`. Deploy the move with no old server still
    running, so the two locks never guard the same ledger.

11. **Admin pages.** Its AI admin calls AbpAi's: aliases (`SetAliasAsync` for each, a target by the model's connection
    and id; `DeleteAliasAsync` for those its page dropped), project access (`SetScopeAccessAsync`), limits
    (`SetLimitAsync` with a measure; `DeleteLimitAsync`), usage (`AiUsageService`). Its models page stays its own.

12. **Settings.** Its configuration binds to `AbpAi` (the old names still read for one release).

Pin it with its existing end-to-end tests: they must pass unchanged before and after.
