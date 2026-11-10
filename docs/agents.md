# AbpAgent

`AbpAi.Agents` makes AbpAi the model behind AbpAgent. Depend on `AbpAiAgentsModule`; it replaces `IAgentModels` with
`AiAgentModels`.

A run's call goes through `IAi` with the agent's alias, the agent's account (`AccountId`) and the run's scope, its
purpose (`agent:{key}`) and its item (`About`). So the agent's own `account` limits apply, aliases come from the run's
scope, and usage says which agent asked about what.

Two things AbpAgent's request doesn't carry yet; give `IAiAgentCalls` to supply them:

- **The person the run is for** (`OnBehalfOf`): so per-person limits count the run against them. Until AbpAgent's
  request carries the run's starter, a host can find it from the run.
- **The data class** the conversation carries: Insight's comes from the model set's policy. With none, the call
  counts as the highest class and goes only where everything may.

A refusal ends the run with the gateway's code (`no_model`, `over_limit`, `data_not_allowed`, `provider`) as
`AgentModelRefusedException`.

Streaming for AbpAgent's live chats (A1) comes when `IAgentModels` has a streaming call; `IAi.StreamAsync` is ready
for it.
