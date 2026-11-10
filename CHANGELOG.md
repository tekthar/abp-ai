# Changelog

All notable changes to AbpAi are written here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the packages follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html): before 1.0, a minor version may change the API.

## [Unreleased]

## [0.2.0] - 2026-11-10

The first release, at the same version as AbpGuard and AbpAgent.

### Added

- `AbpAi.Contracts`: `IAi` (`CompleteAsync`, `StreamAsync`, `ResolveAsync`), `AiRequest`, `AiReply`, `AiStreamEvent`,
  `AiChoice`, `AiRefusedException` (`no_model`, `over_limit`, `provider`, `data_not_allowed`) with `AiLimitRef`,
  `IAiProvider`, `IAiDemoResponder`, `AiMessages`, `AiDataClasses`, and the host's seams `IAiScopes`, `IAiRights`,
  `IAiAccounts`, `IAiNotices`.
- `AbpAi`: the gateway (`AiService`), aliases through the host's scope chain (`AiRouter`), the usage ledger with
  limits per scope, per person, per group and per account (`AiLedger`), data rules (`AiDataRules`), the admin API
  (`AiAdminService`), usage queries and CSV export (`AiUsageService`), the demo model's script (`AiScript`),
  providers for Anthropic, OpenAI-compatible APIs and the demo model, and housekeeping. Tables in schema `ai`.
- `AbpAi.Agents`: AbpAgent's `IAgentModels` on `IAi` (`AiAgentModels`, `AbpAiAgentsModule`, `IAiAgentCalls`), on
  `AbpAgent.Contracts` 0.2.0.
- A sample host (an organization's departments over projects) and its tests on PostgreSQL.
