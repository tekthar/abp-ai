# Moving a product onto AbpAi

For the product the gateway came from (its SDK's `IAi`, its platform's `AiService`). Done when it runs on AbpAi with
no change to its calls, aliases, limits or usage history. Its code wasn't reachable when AbpAi was written, so each
step below names what to check against it.

1. **Calls.** Keep the product's `IAi` as a thin type over `AbpAi.IAi`, or move its callers to `AbpAi.IAi` by
   namespace only. Map its request's project to `ScopeId`, its account and purpose as they are, and its record to
   `About`. Check: each call site's request maps field for field; its refusal codes are `no_model`, `over_limit`,
   `provider` (unchanged), with `data_not_allowed` new.
2. **Scopes.** An `IAiScopes` whose `ParentOfAsync` answers null for every project: one level, as today.
3. **Rights.** An `IAiRights` over its Casl abilities: site admin, and a project's admins.
4. **Tables.** One migration, under its startup lock, moves its AI tables to schema `ai` and renames them
   (`connections`, `aliases`, `limits`, `usage`, `prompts`, `site`), `project_id` to `scope_id`. Usage rows gain
   `scope_path` (`ARRAY[scope_id]`, or empty for the site's) and `state` (`done`). Check its columns against
   `AiModel.ConfigureAi` and write the mapping in the migration.
5. **Data classes.** Existing connections get every class (`public` … `sensitive`), so calls that state none still
   reach them and nothing changes for its users. Then the product states classes where it knows them and the site
   narrows public connections.
6. **Limits.** Its site, project and account limits become `total` on the site, `total` on a scope, and `account`.
7. **The demo model.** Its demo responders implement `AbpAi.IAiDemoResponder` (the same shape) and register as before.
8. **The ledger's lock.** Its old constant goes; AbpAi's is `AiLedger.LockKey`. Deploy the move with no old server
   still running, so the two locks never guard the same ledger.
9. **Settings.** Its configuration binds to `AbpAi` (the old names still read for one release).

Pin it with its existing end-to-end tests: they must pass unchanged before and after.
