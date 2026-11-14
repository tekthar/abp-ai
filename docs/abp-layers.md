# For ABP developers: where ABP's layers are

AbpAi is an ABP module, but it isn't split into ABP's standard module layers. This page maps one onto the other, so
you can find things where you'd look for them.

| ABP's layer (`abp new -t module`) | In AbpAi |
|---|---|
| `*.Domain.Shared` (constants, enums, error codes) | `AbpAi.Contracts`: the records `IAi` takes and gives, `AiRefusalCodes`, `AiDataClasses`; `AiErrorCodes` is in `AbpAi` |
| `*.Domain` (entities, domain services) | `AbpAi`: `Entities.cs` (connections, aliases, limits, the usage ledger, exports), `AiRouter`, `AiLedger`, `AiDataRules` |
| `*.Application.Contracts` (DTOs, service interfaces) | `AbpAi.Contracts` (`IAi`, `IAiProvider`, the host's seams) and the records beside the services in `AbpAi` (`AiAliasInfo`, `AiLimitInput`…) |
| `*.Application` (application services) | `AbpAi`: `AiService` (`IAi`), `AiAdminService`, `AiUsageService`, `AiExportService` |
| `*.EntityFrameworkCore` | `AbpAi`: `IAiDbContext` and `modelBuilder.ConfigureAi()` (`AiModel.cs`), schema `ai`; the host's own context and migrations hold the tables |
| `*.HttpApi` | `AbpAi.HttpApi`: the admin API's controllers, under a prefix the host sets ([ADR 0002](adr/0002-the-admin-api.md)) |
| `*.HttpApi.Client` | Not yet: the one client is `@abpjs/ai`, generated from the OpenAPI document |
| `*.Web` / `*.Blazor` | `@abpjs/ai` (React), released with the packages |
| An integration package | `AbpAi.Agents`: AbpAgent's models and embeddings on `IAi` |

## What works the ABP way

- **Modules:** `AbpAiModule`, `AbpAiHttpApiModule`, `AbpAiAgentsModule`, each with `[DependsOn]`.
- **The database:** replace `IAiDbContext` with your own context (`ReplaceDbContext<IAiDbContext>()`), call
  `modelBuilder.ConfigureAi()` in its `OnModelCreating`, and add a migration as for your own tables.
- **Options:** `AiOptions`, read from the `AbpAi` section of configuration (`appsettings.json`).
- **Replacing a part:** every seam is a service you replace in `ConfigureServices`
  (`context.Services.Replace(ServiceDescriptor.Transient<IAiRights, MyRights>())`).
- **Errors:** `UserFriendlyException` with `AbpAi:*` codes (`AiErrorCodes`); `AbpAi.HttpApi` maps them to 403, 400
  and 409 in `AbpExceptionHttpStatusCodeOptions`.
- **Background work:** `AiHousekeeping`, a hosted service (settling reservations a stopped server left, deleting old
  prompts and exports).

## Where it differs, and why

- **Services, not application services:** `AiAdminService` and the others are plain services that take the actor
  (`AiActor`) as a parameter, so workers and agents call them with the same checks as people; there are no
  `IAiAppService` interfaces and no auto API controllers.
- **Rights are `IAiRights`, not ABP permissions:** who administers what is per scope (a project's admins, and those
  above it), which ABP's permission definitions can't say. Your implementation can read ABP permissions underneath.
- **One main package:** domain, services and EF Core are in `AbpAi`, so a host adds one module, not five. A split into
  ABP's layers is a decision for before 1.0.
- **Texts:** messages are in English in code; there are no localization resources yet.
- **Settings:** the site's AI settings (prompts kept, data classes, whether scopes may connect) are rows in schema `ai`,
  set through `AiAdminService`, not ABP's setting management.

Possible later additions: ABP permission definitions a host may map onto `IAiRights`, and `AbpAi.HttpApi.Client`
([ADR 0002](adr/0002-the-admin-api.md)).
