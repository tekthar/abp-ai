# Usage

Every call is on the ledger: when, where (its scope and every scope above it), whose (its account, and whom it was
for), what asked (`Purpose`, such as `skill:triage@3` or `agent:ask`), what it was about (`About`), the alias, the
connection, the model, its data class, tokens, cost and time.

`AiUsageService`:

- `QueryAsync(actor, query)`: from, to (up to 400 days), a scope (everything under it) or the whole site, grouped by
  `scope` (the scopes right under the one asked about), `person`, `account`, `purpose`, `model`, `connection`, `day`
  or `data-class`, and filtered by account, purpose (`agent:*` matches a start) or model.
- `MineAsync(actor, ...)`: the actor's own, by them or for them.
- `ExportCsvAsync(actor, query)`: the calls themselves, as CSV, up to 100,000.

The site's admins read the whole site. A scope's admins read their scope and those under it, and nothing outside it:
a department's admin sees their month by project and by purpose, and is refused for another department or the site.

Only calls that answered count. Refused calls never reach the ledger; failed ones cost nothing.
