# Hosting

## The module and the tables

Depend on `AbpAiModule`, map the tables in your context, and say your context replaces the gateway's:

```csharp
[DependsOn(typeof(AbpAiModule))]
public class MyHostModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<MyDbContext>(o => o.ReplaceDbContext<IAiDbContext>());
    }
}

[ReplaceDbContext(typeof(IAiDbContext))]
public class MyDbContext : AbpDbContext<MyDbContext>, IAiDbContext
{
    public DbSet<AiConnection> AiConnections => Set<AiConnection>();
    public DbSet<AiAlias> AiAliases => Set<AiAlias>();
    public DbSet<AiLimit> AiLimits => Set<AiLimit>();
    public DbSet<AiUsageRecord> AiUsage => Set<AiUsageRecord>();
    public DbSet<AiPromptRecord> AiPrompts => Set<AiPromptRecord>();
    public DbSet<AiSite> AiSites => Set<AiSite>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.ConfigureAi();
    }
}
```

Your migrations make the tables, in schema `ai`: `connections`, `aliases`, `limits`, `usage`, `prompts`, `site`,
`scope_access` and `exports`. (`IAiDbContext.AiScopeAccess` and `AiExports` have defaults, so a context written for an
earlier version needn't name them.) The connections' and exports' keys are named `PK_ai_connections` and
`PK_ai_exports`, apart from a host's own `connections` or `exports` table. The ledger needs PostgreSQL (an advisory
lock and a `uuid[]` path with a GIN index).

Tests of your own on another database (SQLite, EF Core's in-memory one) can't make the gateway's columns: leave its
tables out of that model with `builder.IgnoreAi()` in place of `ConfigureAi()`, and test what calls models on
PostgreSQL, as `tests/AbpAi.Host.Tests` does.

On each new version, add a migration as you do for your own model (`dotnet ef migrations add AbpAi030`). The model
gives each new column a default that keeps what's there working as before: limits count `cost`, the site's scopes are
`open`, connections' models have no settings. The sample does just this: its `Migrations` folder holds `AbpAi020`
(0.2.0's tables) and `AbpAi030` (what 0.3.0 adds) and `AbpAi040` (what 0.4.0 adds), and its tests migrate a database of 0.2.0's tables, with a site's
rows in it, to the latest.

Keys are encrypted with ABP's string encryption: set `StringEncryption:DefaultPassPhrase` to a secret of your own.

## The seams

| Seam | Give it when | Default |
|---|---|---|
| `IAiScopes` | your scopes nest, or you want their names in usage | one level |
| `IAiRights` | always: who administers the site and each scope; who owns the site (`IsSiteOwnerAsync`, for exports of calls); who administers an agent's account (`AdministersAccountAsync`, for its own limits) | no one |
| `IAiAccounts` | you set limits on groups | no groups |
| `IAiNotices` | admins should hear of limits nearing (`LimitNearingAsync`) and reached (`LimitReachedAsync`) somewhere other than the log | the log |
| `IAiAudit` | exports of calls should be in your audit log | the log |
| `IAiConnectionSource` | you keep connections of your own (below) | AbpAi's `connections` table (`StoredAiConnections`) |
| `IAiProvider` | you need a provider that isn't built in | Anthropic, OpenAI-compatible, demo |
| `IAiDemoResponder` | your tests answer for the demo model in their own way | `AiScript` |

`IAiScopes.ParentOfAsync` is asked for each scope up the chain on every call; cache it if it's costly. A loop, or a
chain deeper than `MaxScopeDepth` (32), is refused.

## Settings (`AbpAi` section)

| Setting | Default | What |
|---|---|---|
| `DataClasses` | public, internal, aggregates, rows, sensitive | the site's data classes, lowest to highest |
| `NoticeAt` | 0.8 | when admins hear a limit nears |
| `ReservationTimeout` | 00:30:00 | how long a reservation no one settles holds |
| `CharsPerToken` | 4 | for a call's estimate |
| `HousekeepingEvery` | 00:10:00 | letting go stale reservations and old prompts |
| `ProviderTimeout` | 00:10:00 | the longest a whole call may take (its total timeout), and a provider to list its models, for a connection that sets none |
| `IdleTimeout` | 00:01:00 | the longest wait for the next part of a streamed reply, its first included (its idle timeout), for a connection that sets none |
| `ProviderRetries` | 2 | how many times the built-in providers try again on 429, 500, 502, 503, 504 and 529, before a stream begins |
| `CapabilitiesDecide` | true | what models can do (tools, images, a schema) decides which may answer; false when your models' flags are only for people to read |
| `ExportDays` | 7 | how long an export of calls is kept |
| `MaxExportDays`, `MaxExportCalls`, `MaxExportBytes` | 31, 100,000, 200 MB | the most one export covers and holds |
| `DemoPacing` | 00:00:00 | the demo model's pause between the pieces of a streamed reply |
| `MaxScopeDepth` | 32 | the longest scope chain |

The site's own settings (whether scopes may connect, which classes their connections may receive, how scopes use the
site's models, whether prompts are kept and for how long) are in the database, set through
`AiAdminService.SetSiteAsync`. A `ScopeAccess` left null there keeps the one that's set.

## Connections

`AiAdminService.CreateConnectionAsync(actor, scopeId, input)`: a name, a provider (`anthropic`, `openai`, `demo`, or a
host's own kind, which may carry its prefix: `acme:bedrock`), its models (the first that's on is its default), the
data classes it may receive, labels, prices per million tokens (and per model; cache writes and reads too, by default
1.25 times and a tenth of the input price), whether it follows a schema, its key, its models' settings, and its
timeouts. Provider settings:

| Provider | Endpoint | Settings |
|---|---|---|
| `anthropic` | empty for `https://api.anthropic.com`, or a gateway's | `anthropic-version` (2026-10-21), `anthropic-beta`, `auth` = `bearer` |
| `openai` | the API's base: `https://api.openai.com/v1`, `http://localhost:11434/v1`, `https://{name}.openai.azure.com/openai/deployments/{deployment}` | `api-version` (Azure), `auth` = `api-key` (Azure), `max-tokens-field` |
| `demo` | none | none |

### Timeouts

Each connection may set two (`IdleTimeoutSeconds`, `TotalTimeoutSeconds`; null keeps what's set, 0 goes back to the
site's `IdleTimeout` and `ProviderTimeout`):

- **Idle**, 1 second to an hour: the longest wait for the next part of a streamed reply, its first event included. A
  local model on modest hardware that needs minutes for its first token sets minutes here; a cloud model's connection
  keeps the site's minute. A call that isn't streamed gets its reply whole, so only its total applies to it.
- **Total**, 1 second to a day: the longest the whole call may take.

A call that runs out is refused with `timeout`, and `AiRefusedException.TimedOut` says which (`idle`, `total`); a
stream ends with it. Its usage row is `failed`, with code `timeout`, at no cost. A host that keeps its own connections
gives them in `AiConnectionDescriptor.IdleTimeout` and `TotalTimeout`.

### Testing a connection

`AiAdminService.TestConnectionAsync(actor, scopeId, connectionId, model)` makes one small call (a few tokens) to the
model named, else the connection's first that's on, straight to its provider, within its idle timeout and with no
second try. `AiConnectionTest` says whether it answered and how long it took, or the provider's status and its own
reason: "invalid x-api-key", "model: claude-x", "It couldn't be reached: …". The key is never in it, nor anything
shaped like one. Nothing goes on the ledger. Save a connection with `Test = true` and `AiConnectionInfo.Test` carries
how its test went, so an admin page tests on save; it works for connections a host keeps, too.

### Models and what they can do

`ModelSettings`, by the provider's id for each model: `Name` (what aliases and calls call it; its id when empty),
`Enabled`, `Tools` (it takes tools), `Images` (it takes images), `StructuredOutput` (null: as the connection says),
`ContextTokens` and `PriceSetAt` (set when its price changes). A call names a model by its name or its id; the router
passes over models that are off, and, of an alias's targets, those that can't do what the call needs: tools when it
has tools, images when a message has an image block, a schema when it gives one. A model with no settings is on and
takes everything, as every model did before models had settings. Settings left null when a connection is changed keep
the ones there.

`AiAdminService.DiscoverModelsAsync(actor, connectionId)` asks the provider which models it offers and adds the ones
the connection doesn't list yet, each off until an admin turns it on. Anthropic lists `GET /v1/models`; an
OpenAI-compatible server `GET {endpoint}/models`; the demo model its one, `demo`. A provider of your own lists its
models by implementing `IAiProvider.ListModelsAsync` (the default says it can't). `ProviderModelsAsync(actor,
scopeId, connectionId)` asks without adding anything, for connections you keep yourself.

### Connections you keep yourself

A product with a connections page of its own (its connections, their secrets, its own rows for their models) keeps
them, and gives the gateway an `IAiConnectionSource` that reads them. Aliases, limits, usage and the data rules then
refer to its connections by their ids, in AbpAi's tables.

```csharp
public sealed class MyConnections(MyDbContext db, ISecretStore secrets) : IAiConnectionSource
{
    // The site's connections, and those set on the scopes named (a call's scope and those above it).
    public async Task<IReadOnlyList<AiConnectionDescriptor>> ListAsync(IReadOnlyCollection<Guid> scopeIds, CancellationToken ct)
    {
        var rows = await db.Connections.Where(c => c.Type == "anthropic" && (c.ProjectId == null || scopeIds.Contains(c.ProjectId.Value)))
            .Select(c => new { c.Id, c.ProjectId, c.Name, c.BaseUrl, Models = db.Models.Where(m => m.ConnectionId == c.Id).ToList() })
            .ToListAsync(ct);
        return [.. rows.Select(c => new AiConnectionDescriptor(c.Id, c.ProjectId, c.Name, "anthropic")
        {
            Endpoint = c.BaseUrl,
            Models = [.. c.Models.Select(m => m.ModelId)],
            ModelSettings = c.Models.ToDictionary(m => m.ModelId, m => new AiModelSettings { Name = m.Name, Enabled = m.Enabled, Tools = m.Tools, Images = m.Images }),
            ModelPrices = c.Models.ToDictionary(m => m.ModelId, m => new AiPrice(m.InputPrice, m.OutputPrice)),
            DataClasses = [.. AiDataClasses.Default],
            HasKey = true,
        })];
    }

    // Asked only as a call goes out through the connection; never shown.
    public Task<string?> KeyAsync(Guid connectionId, CancellationToken ct) => secrets.GetAsync(connectionId, "apiKey", ct);
}

context.Services.Replace(ServiceDescriptor.Transient<IAiConnectionSource, MyConnections>());
```

With a source of your own, `AiAdminService.ListConnectionsAsync` lists yours, and `CreateConnectionAsync`,
`UpdateConnectionAsync`, `DeleteConnectionAsync` and `DiscoverModelsAsync` are refused with
`AiErrorCodes.HostConnections`: connections are set where you keep them. The rules that bind scopes' connections still
hold: a scope's connection serves only while the site lets scopes connect, and receives only the classes the site lets
scopes' connections receive.

## Which site models a scope may use

By default (`ScopeAccess` = `open`) every scope uses every one of the site's connections, and connects its own while
the site lets scopes connect. Set it to `listed`, and a scope uses the site's models only as its access says
(`AiAdminService.SetScopeAccessAsync(actor, scopeId, new AiScopeAccessInput(AllSiteModels, SiteModels, MayConnect))`):
every one of the site's models, or a list of `(connection, model)`; and connections of its own only when the site lets
scopes connect and its access lets it too. A scope with no access of its own has the nearest one above it; one with
none anywhere above it may use none of the site's models. Calls on the site itself use every site model. Only the
site's admins set access (`GetScopeAccessAsync`, `ListScopeAccessAsync`, `SetScopeAccessAsync`, `DeleteScopeAccessAsync`),
and a scope's alias may lead only to the site's models open to it.

## Calls: system blocks and provider options

`AiRequest.SystemBlocks` takes the system prompt as the Messages API's blocks (with `cache_control`, for a provider's
prompt cache); it wins over `System`. `AiRequest.ProviderOptions`, a JSON object, adds to the provider's request what the
gateway doesn't set itself (`thinking`, `temperature`, `tool_choice`, `metadata`); the model, messages, system prompt,
tools, most tokens and streaming are always the gateway's. Anthropic gets both as they are. The OpenAI-compatible
provider joins the blocks' text into its system message, and takes the options in its own shape: `tool_choice` (the
Messages API's `any`, `none`, `tool` become `required`, `none`, a function) only when the call has tools,
`stop_sequences` as `stop`, and `thinking` and `output_config` left out; the rest go as they are. A provider of your
own gets the whole call as `AiProviderCall.Request`.

## The API

The admin and usage services are the API: `AiAdminService` (site settings, connections, models, scopes' access,
aliases, limits), `AiUsageService` (queries, a person's own, recent calls, CSV) and `AiExportService` (exports of calls
for support). Their refusals are `UserFriendlyException`s with `AiErrorCodes.Forbidden` (403), `Invalid` (400) or
`HostConnections` (409).

`AbpAi.AspNetCore` serves them over HTTP ([ADR 0002](adr/0002-the-admin-api.md)): depend on `AbpAiAspNetCoreModule`.

| Route (under `AiHttpOptions.RoutePrefix`, `api/ai` by default) | What |
|---|---|
| `GET`, `PUT site` | The site's settings |
| `GET`, `POST connections`, `PUT`, `DELETE connections/{id}` | Connections, the site's or a scope's (`?scopeId=`); 409 when the host keeps them |
| `POST connections/{id}/test`, `GET connections/{id}/provider-models`, `POST connections/{id}/discover` | Trying one, and its provider's models |
| `GET aliases`, `PUT`, `DELETE aliases/{name}` | Aliases and their targets (`?scopeId=`) |
| `GET`, `POST limits`, `DELETE limits/{id}`; `GET`, `POST accounts/{id}/limits` | Limits per scope, per person or group, and an account's own |
| `GET scopes/access`, `GET`, `PUT`, `DELETE scopes/{id}/access` | Which site models a scope may use |
| `GET resolve/{alias}` | Where an alias leads now (`?scopeId=&dataClass=`), for its admins; 422 with the gateway's code when nothing may take it |
| `POST usage`, `GET usage/mine`, `POST usage/recent`, `GET usage/mine/recent`, `POST usage/csv` | Usage |
| `GET`, `POST exports`, `POST exports/{id}/file`, `DELETE exports/{id}` | Exports of calls, for the site's owner |

- **Who:** every action wants a signed-in caller, or `AiHttpOptions.Policy`; what they may do is the services' check.
  The actor is `IAiCurrentActor`'s: the signed-in user's id by default; replace it when your `IAiRights` reads more.
- **The password again:** the actions that change settings or hand out data are `[AiSensitive]`;
  `AiHttpOptions.Sensitive` adds what that means for you (AbpGuard's `StepUpAttribute`, say). `Configure` reaches every action.
- **OpenAPI:** the controllers are visible to the API explorer, in your own document by default or in
  `AiHttpOptions.OpenApiGroupName`'s.
- **Errors:** the module maps the gateway's codes to their statuses, and answers every refusal with ABP's error JSON,
  whatever the caller accepts.
- They're plain controllers, not application services, so AbpMcp never makes tools of them.

The sample host serves them under `api/ai`, with its own `ask` beside them.
