# 0002. The admin API as a package (AbpAi.HttpApi)

- **Status:** Accepted (the user's go, 14 Nov 2026)
- **Date:** 2026-11-14
- **Revisit at:** when a second UI besides @abpjs/ai calls it, or AbpAi 1.0

## Context

AbpAi's admin and usage are services (`AiAdminService`, `AiUsageService`, `AiExportService`), and each host maps them
to HTTP itself: the sample host's `AiController`, one product's settings controller under `api/ask/ai`, and the first
product's own admin under `api/platform/ai`. The three are near copies that already differ in routes and shapes.
The user asked for the settings screens to ship beside the packages (`@abpjs/ai`), so one UI needs one API.

The services already check every right through the host's `IAiRights` (the site's admins, a scope's admins and those
above it, an account's administrators, the site's owner for exports) and refuse with `AiErrorCodes`. What a host still
writes is the mapping: routes, who the actor is, which actions need the password again, and the HTTP status of a code.

## Decision

**A new package, AbpAi.HttpApi, carries the admin API.** A host gets it by depending on `AbpAiHttpApiModule`;
AbpAi itself stays free of ASP.NET, for workers. Released with AbpAi at its version (0.7.0). The name is ABP's for a
module's controllers (`*.HttpApi`), as AbpAgent's `AbpAgent.HttpApi` is.

- **Routes under a prefix the host sets** (`AiHttpOptions.RoutePrefix`, default `api/ai`), so a product keeps its
  addresses (`api/ask/ai`, `api/platform/ai`).
  - `site`;
  - `connections` (list, create, update, delete, `{id}/test`, `{id}/provider-models`, `{id}/discover`);
  - `aliases` and `aliases/{name}`;
  - `limits`, and `accounts/{id}/limits`;
  - `scopes/access` and `scopes/{id}/access`;
  - `resolve/{alias}`;
  - `usage` (a query, `mine`, `recent`, `csv`);
  - `exports` (list, create, `{id}/file`, delete).
  A scope is named by its id (`scopeId`), as the services name it.
- **The actor** comes from `IAiCurrentActor`: by default the signed-in user's id (`ICurrentUser`); a host whose `IAiRights`
  reads more (an ability, a principal) gives its own.
- **Who may:** every action needs a signed-in caller, or the host's policy (`AiHttpOptions.Policy`); what they may do
  is the services' check, unchanged.
- **The password again:** the actions that change settings or hand out data carry `[AiSensitive]`, and the host says
  what that means (`AiHttpOptions.Sensitive`: AbpGuard's step-up, say). By default nothing more.
- **Errors:** the module maps `Forbidden` to 403, `Invalid` to 400 and `HostConnections` to 409, so a host doesn't.
- **OpenAPI:** the controllers are visible to the API explorer (ABP hides controllers that aren't remote services). Their
  group is `AiHttpOptions.OpenApiGroupName`: null (the default) puts them in the host's own document, a name such as
  `abpai` in a document of their own, from which `@abpjs/ai` generates its client.
- **Plain MVC controllers, not application services:** AbpMcp makes tools only of application services marked
  `[McpTool]`, so the endpoints that handle keys never become an agent's tools.

`IAiRights` needs no new member: `AdministersAccountAsync` (an agent's limits set by its project's admins) closed the
gap that once made the first product write the gateway's tables itself.

### Where it differs from an ABP module's HttpApi, and why

- **The host sets the prefix,** not a fixed `api/ai` with `[RemoteService]`'s area: the two products already serve
  these settings at addresses of their own, which their web apps, phone apps and published contracts use. A fixed route
  would break both, or leave each with a second copy of the API.
- **Rights are `IAiRights`, not ABP's permission definitions:** who administers what follows the host's own model (a
  site's admins, a scope's admins and those above it, an account's administrators, a site's owner), which differs per
  product and per scope. The services check it on every call, workers and agents included, not only over HTTP. ABP's
  permissions are per user or role, without the scope, so they can't say "the admins of this project".
- **Plain controllers, no `[RemoteService]` and no application-service interface:** ABP would then also generate
  dynamic proxies and API definitions for them, and AbpMcp (`[McpTool]` on application services) is kept away from the
  endpoints that handle keys by design, not by an attribute someone could forget.

### Possible later additions (not part of 0.7.0)

- **ABP's permission model:** permission definitions (`AbpAi.Site.Manage` and the like) that a host may map onto
  `IAiRights`, for hosts whose rights are roles and grants already, so they configure rather than implement the seam.
- **`AbpAi.HttpApi.Client`:** typed C# proxies of the API, for another .NET service that manages the gateway remotely.
  Today the one client is `@abpjs/ai`, generated from the OpenAPI document.

## Steps

1. The package: its module, `AiHttpOptions`, `IAiCurrentActor`, the route convention, `[AiSensitive]`, the error codes.
2. The controllers, over the services as they are.
3. Tests: a host on the package (routes under a prefix, the actor, a refusal's status, the sensitive hook, a host's own
   connections refused with 409).
4. The sample host on the package instead of its own controller; README, `docs/hosting.md`, CHANGELOG.
5. Release 0.7.0. AbpAi.HttpApi is a new id: if nuget.org refuses its first push, the user's policy step, then a rerun.
6. The hosts move onto it: the product with a settings controller of its own drops it; the first product follows when
   `@abpjs/ai` replaces its admin pages.

## Consequences

- One API for the screens; a host writes options, not controllers.
- A host that wants the endpoints only partly can still call the services itself; the package's controllers are all or
  nothing per host.
- The first product keeps its own admin until `@abpjs/ai` exists: its pages use shapes of their own (its connections and
  models stay its own), so its switch is a step with the UI, not with this package.
