# AbpAi

The model gateway for ABP applications on .NET 10 and PostgreSQL. Every host gets aliases, limits, usage and
providers from one package, and rules for which data may go to which provider. It is also the default model behind
AbpAgent.

> **Preview.** 0.x versions may change their API in any minor version (SemVer). Pin an exact version.

## What it does

- **One call:** `IAi.CompleteAsync` (one whole reply), `StreamAsync` (the reply as it's written), `ResolveAsync` (which
  model an alias leads to). Messages, tools, replies and stream events are in the Messages API's shape; providers
  translate to and from it.
- **Aliases:** `fast`, `smart`, `local`. The call's scope's first, then each scope above it, then the site's, else a
  model by its name. An alias has targets in order; the ones after the first are its fallbacks.
- **Providers:** Anthropic, OpenAI-compatible (OpenAI, Azure OpenAI, Ollama, vLLM, most gateways) and a demo model.
  Others plug in through `IAiProvider`.
- **Connections** for the site, and for each scope when the site lets it. Keys are encrypted and never shown.
- **Scopes the host defines** (`IAiScopes`): projects, model sets, org units. Scopes nest. A call counts against its
  scope and every scope above it, and an alias or connection set on a scope applies to the scopes under it.
- **Limits** over a day, a week or a month: a scope's total (the site's too), what each person may spend (with
  overrides for named accounts and groups, no row per person), and a named account's own (an agent's). Each call's
  estimate is reserved on a usage ledger under one lock, so calls at the same time can't go over. A refusal names the
  limit and when it frees. Admins hear when a limit reaches 80%.
- **Which data may go where:** the site's data classes (`public`, `internal`, `aggregates`, `rows`, `sensitive`), each
  connection's list of what it may receive and labels for where it runs, and each call's highest class. A call that
  doesn't fit is refused with `data_not_allowed` before it leaves, or falls back to a target that may receive it. A
  call that states no class counts as the highest. Prompts aren't kept unless the site turns that on, for a period.
- **Usage people can read:** by scope, person, account, purpose, model, connection, day and data class, and export as
  CSV. A scope's admins see their scope and those under it, nothing else; each person sees their own.
- **The demo model as a test tool:** `AiScript` answers for it with scripted replies (from code or a JSON file), so
  features that use AI, and evals, run in CI with no provider.

## Packages

| Package | Holds | Depends on |
|---|---|---|
| `AbpAi.Contracts` | `IAi` and its records, refusals, `IAiProvider`, `IAiDemoResponder`, the host's seams | Plain .NET |
| `AbpAi` | The service, the ledger, limits, notices, connections, the admin and usage services, the built-in providers; tables in schema `ai` | ABP, EF Core (PostgreSQL) |
| `AbpAi.Agents` | AbpAgent's `IAgentModels` on top of `IAi`, so neither package needs the other | Both (not on nuget.org until AbpAgent's contracts are) |

## Getting started

```csharp
[DependsOn(typeof(AbpAiModule))]
public class MyHostModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddAbpDbContext<MyDbContext>(o => o.ReplaceDbContext<IAiDbContext>());
        context.Services.Replace(ServiceDescriptor.Transient<IAiScopes, MyScopes>());   // who a scope's parent is
        context.Services.Replace(ServiceDescriptor.Transient<IAiRights, MyRights>());   // who administers what
    }
}

// In MyDbContext.OnModelCreating:
modelBuilder.ConfigureAi();
```

Then call a model:

```csharp
AiReply reply = await ai.CompleteAsync(new AiRequest("smart", AiMessages.User("Describe the orders table."))
{
    ScopeId = modelSetId,             // the host's scope
    AccountId = currentUserId,        // whose limits
    DataClass = AiDataClasses.Internal,
    Purpose = "describe-model",       // what asked, for usage
    MaxTokens = 800,
});
```

See [hosting](docs/hosting.md), [data rules](docs/data-rules.md), [limits](docs/limits.md), [usage](docs/usage.md),
[testing with the demo model](docs/testing.md), [AbpAgent](docs/agents.md), and [moving a product onto
AbpAi](docs/moving-onto-abp-ai.md). Why it is shaped this way, and the answers to the open questions, are in
[ADR 0001](docs/adr/0001-abp-ai-as-a-package.md).

## Building

```bash
dotnet build AbpAi.slnx
```

`AbpAi.Agents` builds against AbpAgent's contracts from a checkout of `todahq/toda-agents` beside this repository
(`../toda-agents`), or wherever `AbpAgentContractsDir` points. The host tests need PostgreSQL on localhost:5432
(postgres/postgres), or the server `ABP_AI_TEST_POSTGRES` names.

```bash
dotnet test AbpAi.slnx
```

## Releasing

Set `VersionPrefix` and `VersionSuffix` in `Directory.Build.props`, move CHANGELOG.md's Unreleased to the version, and
push a tag `v<version>` (`v0.1.0-preview.1`). CI runs the checks, then pushes `AbpAi` and `AbpAi.Contracts` to
nuget.org through Trusted Publishing (the policy names this repository, `ci.yml` and the `release` environment; the
repository variable `NUGET_USER` is the nuget.org account) and makes a GitHub release.

## License

Apache 2.0. See [LICENSE](LICENSE).
