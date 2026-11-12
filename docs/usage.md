# Usage

Every call is on the ledger: when, where (its scope and every scope above it), whose (its account, and whom it was
for), what asked (`Purpose`, such as `skill:triage@3` or `agent:ask`), what it was about (`About`), the alias, the
connection, the model, its data class, how it went (`done`, `failed`, `refused`, and while it runs, `reserved`) and
why (its code: `over_limit`, `no_model`, `data_not_allowed`, `timeout`, `provider`), tokens (in, written to the
provider's cache, read from it, out), cost and time.

`AiUsageService`:

- `QueryAsync(actor, query)`: from, to (up to 400 days), a scope (everything under it) or the whole site, grouped by
  `scope` (the scopes right under the one asked about), `person`, `account`, `purpose`, `model`, `connection`, `day`
  or `data-class`, and filtered by account, purpose (`agent:*` matches a start) or model.
- `MineAsync(actor, ...)`: the actor's own, by them or for them.
- `RecentAsync(actor, query, count)`: the latest calls themselves, newest first (up to 500), of every state or those
  the query's `States` name: what an admin page's "latest calls" shows. `MineRecentAsync` for the actor's own.
- `ExportCsvAsync(actor, query)`: the calls that answered, as CSV, up to 100,000.

The site's admins read the whole site. A scope's admins read their scope and those under it, and nothing outside it:
a department's admin sees their month by project and by purpose, and is refused for another department or the site.

Each row counts the calls that answered (`Calls`), with their tokens and cost, and apart from them those that failed
(`Failed`) and were refused (`Refused`), which cost nothing.

## An export for support

When answers go wrong, the site's owner exports a period's calls to send to support: `AiExportService.CreateAsync(actor,
from, to, scopeId)`. Only the owner may (`IAiRights.IsSiteOwnerAsync`; by default no one), and each export is told to
the audit log (`IAiAudit`, action `ai.calls.exported`) before it's kept: when the log can't take it, nothing is
exported. It covers up to 31 days and holds up to 100,000 calls (`Truncated` says when there were more): one line of
JSON per call, oldest first, gzipped, with when, how it went and why, where (the scope's id and key) and whose, what it
went to, its data class, tokens and cost, and its system prompt, messages and reply only where the site kept them
(`KeepPrompts`; `prompt_kept` says). `ListAsync` lists the ones kept, `DownloadAsync` gives the file (told to the audit
log as `ai.calls.downloaded`), `DeleteAsync` removes one; each is deleted after `ExportDays` (7).
