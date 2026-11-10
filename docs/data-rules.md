# Which data may go where

A call says what kind of data it carries; a connection says what kinds it may receive. The gateway refuses any call
that doesn't fit, before it leaves.

## Classes

The site defines its classes, lowest to highest (`AbpAi:DataClasses`). The default:

| Class | What |
|---|---|
| `public` | anything anyone may see |
| `internal` | the organization's own words: names of things, descriptions, questions |
| `aggregates` | totals, counts and averages, with no row of data |
| `rows` | rows of the organization's data |
| `sensitive` | columns marked sensitive: personal data, pay, health |

## A call

`AiRequest.DataClass` is the highest class the call carries. A call that states none, or one the site doesn't define,
counts as the highest: it goes only where everything may go.

## A connection

`DataClasses` lists what it may receive, exactly: a connection that lists `public, internal, aggregates` receives
nothing else. `Labels` say where it runs (`on-premises`, `eu`, `provider-cloud`), for people to read.

A scope's own connection may list only what the site lets scopes' connections receive (`AiSiteSettings.ScopeDataClasses`,
by default `public` and `internal`). That's checked when it's set, and again at every call, so narrowing the site's
setting narrows every scope's connection at once.

## Choosing, and refusing

An alias's targets are tried in order. The first that is on, may receive the call's class, and (when the call gives
an `OutputSchema`) follows a schema, answers. When none may receive it, the call is refused with `data_not_allowed`
and nothing is sent.

So pointing an alias at a public provider can't quietly widen what leaves: the next call that carries more than that
provider may receive is refused (or falls back to a target that may).

## Prompts

The gateway keeps no prompt unless the site turns that on (`KeepPrompts`), and then only for `PromptDays`. Turning it
off deletes the ones kept at the next housekeeping.

## Insight

The data class comes from the model set: each model's policy for what an LLM may see (rows, aggregates or none) and
each column's sensitivity set the call's class. A model set marked "aggregates only" never states more than
`aggregates`, so it can't send rows to any provider; its prompts never carry rows in the first place, and the gateway
holds the line if they ever do.
