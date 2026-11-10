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

Your migrations make the tables, in schema `ai`: `connections`, `aliases`, `limits`, `usage`, `prompts`, `site`.
The ledger needs PostgreSQL (an advisory lock and a `uuid[]` path with a GIN index).

Keys are encrypted with ABP's string encryption: set `StringEncryption:DefaultPassPhrase` to a secret of your own.

## The seams

| Seam | Give it when | Default |
|---|---|---|
| `IAiScopes` | your scopes nest, or you want their names in usage | one level |
| `IAiRights` | always: who administers the site and each scope | no one |
| `IAiAccounts` | you set limits on groups | no groups |
| `IAiNotices` | admins should hear of limits nearing somewhere other than the log | the log |
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
| `ProviderTimeout` | 00:10:00 | how long a provider may take |
| `MaxScopeDepth` | 32 | the longest scope chain |

The site's own settings (whether scopes may connect, which classes their connections may receive, whether prompts are
kept and for how long) are in the database, set through `AiAdminService.SetSiteAsync`.

## Connections

`AiAdminService.CreateConnectionAsync(actor, scopeId, input)`: a name, a provider (`anthropic`, `openai`, `demo`), its
models (the first is its default), the data classes it may receive, labels, prices per million tokens (and per model),
whether it follows a schema, and its key. Provider settings:

| Provider | Endpoint | Settings |
|---|---|---|
| `anthropic` | empty for `https://api.anthropic.com`, or a gateway's | `anthropic-version` (2026-10-21), `anthropic-beta`, `auth` = `bearer` |
| `openai` | the API's base: `https://api.openai.com/v1`, `http://localhost:11434/v1`, `https://{name}.openai.azure.com/openai/deployments/{deployment}` | `api-version` (Azure), `auth` = `api-key` (Azure), `max-tokens-field` |
| `demo` | none | none |

## The API

The admin and usage services are the API: `AiAdminService` (site settings, connections, aliases, limits) and
`AiUsageService` (queries, a person's own, export). Map them to your routes; the sample's `AiController` shows one
way. Their refusals are `UserFriendlyException`s with `AiErrorCodes.Forbidden` (403) or `Invalid` (400).
