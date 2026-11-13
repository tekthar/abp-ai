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

`IAiAgentCalls` also says how the agent's turns are made (`AiAgentCallInfo`):

- **`Cache`**, on by default: the provider may cache each turn's start, since a run re-sends its conversation every turn.
  Anthropic is told where; OpenAI-compatible providers cache without being told. Cached tokens are counted and priced.
- **`MaxTokens`**: the fewest tokens a turn may answer with, a floor on AbpAgent's per-turn `MaxTokens` (thinking takes
  room). The per-call limits still cap it.
- **`ProviderOptions`**: more of the provider's request, such as `thinking`.

An agent's graph memory (AbpAgent.Graphiti) embeds its texts through the same gateway: `AbpAiAgentsModule` registers
`AiAgentEmbeddings` as AbpAgent's `IAgentEmbeddings`, over `IAi.EmbedAsync` ([embeddings.md](embeddings.md)), with the
agent's account, its scope and a purpose of its own (`agent:{key}:memory:embed`), so its limits count those calls too.
`IAiAgentCalls.DescribeEmbedAsync` says the person it's for and the class of data the texts carry (with none they count
as the highest). Give the site an `embed` alias to an embedding model whose vectors are the size the graph is made for
(`Agents:Graph:Dimensions`, 1024 by default).

A refusal ends the run with the gateway's code (`no_model`, `over_limit`, `data_not_allowed`, `timeout`, `provider`)
as `AgentModelRefusedException`, and so does a model that declines to answer (its `stop_reason` is `refusal`), with the
code `refusal`.

A live chat's turn streams (AbpAgent 0.3.0's `IAgentModels.StreamAsync`, A1): the Messages API's events as the model
writes them, then `AgentModelEvent.Finished` with the whole reply, its tokens (in, the cache's, out) and its settled
cost. A refusal before the first event is thrown as `AgentModelRefusedException`; a failure after it (a timeout, the
provider breaking off) is the Messages API's `error` event, its `error.type` the gateway's code, and the stream ends;
so does a model that declines (`refusal`). A stream that fails after it began costs what it used, and the agent's
limits count it.
