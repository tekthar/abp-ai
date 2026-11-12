# Embeddings

Vectors for texts, to search by meaning: an agent's memory, a document's passages. `IAi.EmbedAsync` takes the texts and
an alias, and gives one vector each, in order:

```csharp
AiEmbedding made = await ai.EmbedAsync(new AiEmbedRequest("embed", ["The March invoice is overdue", "Backups run on Sunday"])
{
    AccountId = agentAccount,          // its own limits apply, and the person's it acts for (OnBehalfOf)
    Purpose = "agent:ask:memory",      // for usage
    About = new AiSubject("episode", episodeId),
    DataClass = AiDataClasses.Internal, // what the texts carry; none counts as the highest
    InputType = AiEmbedInputTypes.Document,
});
float[] first = made.Vectors[0];
```

It's a call like any other: the alias leads to a model through the call's scope, then the site, else a model by its
name; the data rules choose the first target that may receive the texts' class, or refuse before anything leaves; the
estimate (the texts' tokens) is reserved against every limit that applies, in cost, tokens and calls; the call is on
the ledger with its purpose and what it was about, and a refusal is too. Only the connection's total timeout applies,
as the vectors come whole. Up to 2,048 texts a call, none empty.

A vector column is sized once (in its migration), so a call may say the size it needs (`Dimensions`): the vectors come
back that size, or the call is refused (`provider`) when the model can't give it. OpenAI's `text-embedding-3` models
shorten theirs; most others give their own size only.

## Models that make them

A model says it makes embeddings in its settings (`AiModelSettings.Embeds`); an embedding call goes only to one that
does. A model found on a connection (`DiscoverModelsAsync`) does when its id says `embed` (`text-embedding-3-small`,
`nomic-embed-text`). A model with no settings takes everything, as for other calls, so set `Embeds` false on a
connection's chat models when an alias might lead to them. Give embedding models an alias of their own (`embed`), and
their input price.

## Providers

| Provider | Embeddings |
|---|---|
| `openai` | `POST {endpoint}/embeddings`: OpenAI, Azure OpenAI (a deployment's endpoint), Ollama (`http://localhost:11434/v1`), vLLM, Voyage AI (`https://api.voyageai.com/v1`, with the setting `input-type` = `send` so a query and a document are embedded as each). `Dimensions` is sent as `dimensions` (OpenAI's `text-embedding-3`). |
| `anthropic` | none: Anthropic's API doesn't make embeddings, so the call is refused with `no_model` before anything leaves. Use an OpenAI-compatible connection for them. |
| `demo` | vectors of 256 places (or `Dimensions`), the same for the same text on every run and machine: each word hashed to a place, then of length one. Texts that share words are near each other, so tests of a search by meaning run with no provider. |

A provider of your own makes them by implementing `IAiProvider.CanEmbed` and `EmbedAsync`.
