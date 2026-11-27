# 0003. Documents people add, read and searched, for more than one product

- **Status:** Accepted
- **Date:** 2026-11-27

## Context

People keep what an organization knows in files: contracts, policies, reports, minutes, scanned letters, photos of
forms. A product whose agent answers questions needs to read them, find the passages that answer a question, and say
where each answer came from (the file and the page), without showing a person anything they may not read. A first
product wants this for its questions over data; a second keeps versioned attachments on its records and assets, and
its agents could read them the same way.

What it takes is the same in both:

- files kept safely, known by their bytes and within limits;
- their text taken out with its page numbers and headings: a PDF's text layer, a Word file's paragraphs, plain text, and
  for scanned pages and pictures a model that reads images;
- the text cut into passages, each with a vector from an embedding model and words for full-text search, in Arabic and
  English;
- the work done in the background, by one server per file, again when the embedding model changes;
- search that ranks only what the person may read, by meaning and by words together;
- the text reaching only models allowed to receive it.

## Decision

**Two packages in this repository: `AbpDocuments` and `AbpDocuments.HttpApi`**, released with AbpAi's version. They
sit above AbpAi and AbpCluster (its lock and its storage) and depend on both; neither depends on them. PostgreSQL only,
with the `vector` extension (pgvector).

### A document

- **Belongs to a scope** (the host's: a project, an org unit; null for the site), has a **data class**, a name, its
  kind known by its bytes, its size and SHA-256, who added it and when, and where its indexing stands: `queued`,
  `reading`, `indexing`, `ready`, `waiting` (a limit, with the time it tries again) or `failed` with the reason.
- **Data classes are AbpAi's** (`public`, `internal`, `aggregates`, `rows`, `sensitive`, and any a host adds through
  `AbpAi:DataClasses`). Which of them a document may have is `DocumentsOptions.DataClasses` (by default the site's
  classes except `aggregates` and `rows`, which are about data, not files). A host that adds `restricted` to AbpAi
  lists it there too.
- **Optionally on something of the host's** (`OwnerKind`, `OwnerId`: a record, an asset), for documents that are
  attachments.
- **Its bytes are the package's or the host's.** Uploads to the library are kept in AbpCluster.Storage, in a store of
  their own (`documents`), under the document's id. A host that keeps files already names a source instead: a document
  is then keyed by `(Source, SourceKey)` with a `SourceVersion`, and the package asks the host's `IDocumentSource` of
  that name for the bytes when it reads them; it never copies them. A new version is read again in place and its
  passages replace the old ones: there's no history, and each passage says which version it came from.
- **Hidden without reading again.** `SetHiddenAsync(source, key, hidden)` takes a document out of search and lists and
  puts it back (a record in the trash and out of it), its passages kept.
- **Uploads are checked by their bytes**, never their name or the type the browser sent: PDF, Word (`.docx`: a zip with
  `word/document.xml`), plain text and Markdown (UTF-8, no NUL), PNG, JPEG and WebP. The most bytes per file and pages
  per document are options (25 MB and 500 pages by default).

### Reading, cutting, embedding

- **Text with its pages.** PdfPig reads a PDF's text layer page by page; lines in a clearly larger font than the
  page's body are its headings. A page with (almost) no text is scanned: its largest image goes to a model that reads
  images. OpenXML reads a Word file's paragraphs, its headings by style, its tables as rows, and starts a page at each
  page break (a Word file keeps no other page numbers). Plain text is a page per form feed, else one page; Markdown's
  `#` lines are its headings. A picture is one page read by the model.
- **Images are read through AbpAi** (`IAi.CompleteAsync`, alias `DocumentsOptions.ReadAlias`, `read` by default), asked
  for the page's text as written, headings marked, in its own language, nothing added.
- **Passages** are cut within a page at paragraph ends, about 1,200 characters with 150 of overlap, each with its page
  and the heading it sits under. A passage never spans two pages, so a citation is one page.
- **Vectors through AbpAi** (`IAi.EmbedAsync`, alias `DocumentsOptions.EmbedAlias`, `embeddings` by default, input type
  `document`). The model that made them is kept on each passage. Vectors are kept as `real[]` and compared with pgvector's
  `cosine_distance`, so models of any size live side by side; only vectors of the question's model are compared.
- **Every call says what it carries and why**: the document's data class, its scope, the account that added it, and the
  purpose `documents:read`, `documents:embed` or `documents:search`, so AbpAi's rules, limits and usage hold and its
  usage screen tells them apart.
- **Words for full-text search**: each passage has a generated `tsvector` of its heading (weighted higher) and its text
  in PostgreSQL's `english` and `arabic` configurations and `simple`, after Arabic's diacritics, tatweel and letter forms
  are made one (alef with hamza or madda, alef maqsura, taa marbuta), with a GIN index.

### In the background

- A worker on every server looks for documents to read every few seconds, and at once on the server that took an
  upload. It takes the document's cluster lock (`IClusterLocks`, `documents:{id}`) first: one server per document, and
  a document whose server stopped is taken by another, since the lock goes with its connection.
- **A limit isn't a failure.** A model call refused for a limit (`over_limit`) or busy (`provider`, `timeout`) leaves the
  document `waiting`, tried again later (a minute, doubling to an hour). Anything else ends `failed` with its reason. A
  failed document is read again by the host's admins (`ReindexAsync`).
- **When the embedding model changes**, a round under the lock `documents:models` asks AbpAi where the alias leads for
  each scope and class among ready documents (`IAi.ResolveAsync`); a document whose passages came from another model is
  embedded again from its kept text, without reading the file again.
- **Deleting** a document removes its passages and pages in the same transaction, then its file.

### Who reads what

- **Scopes, from the host:** `IDocumentsRights.ReadableAsync(accountId)` gives every scope, or a set of them (with or
  without the site's own). It goes into the SQL.
- **Rules of the host's own, also in the SQL:** `IDocumentsFilter.FilterAsync(documents, db, accountId)` gets the
  documents as an `IQueryable` over the host's context (which replaces `IDocumentsDbContext`, so it's the same context)
  and returns them narrowed, such as an `EXISTS` against the host's records for restricted records or a parent's reach.
  It's composed into the same query as the ranking.
- **So passages a person may not read are never ranked.** Search, lists, pages and files all go through the same filter.

### Searching

- **Hybrid.** The question is embedded once per embedding model among the passages the person may read (by the
  model's name, so it meets the same model), the 50 nearest by cosine distance and the 50 best by `ts_rank_cd` over an
  OR of the question's words are taken, and merged by reciprocal rank (k = 60). A model that refuses the question (its
  data class, a limit) leaves its documents to the words alone, and the result says so.
- **Classes on request.** A caller can ask for only some classes (`SearchInput.Classes`): an agent's tool asks for those
  its own model may receive, and passages above them are counted as withheld, not returned.
- **What comes back**: passages with the document's id, name and kind, the page, the heading, the text, the score, its
  class and the ranks that made it.

### The API (`AbpDocuments.HttpApi`)

Under the host's prefix (`DocumentsHttpOptions.RoutePrefix`, `api/documents` by default), every action signed in:

| Action | Route | Who |
| --- | --- | --- |
| List (scope, search in names, status, page) | `GET /` | readable |
| Add a file (multipart: `file`, `scopeId`, `dataClass`) | `POST /` | `MayAddAsync(scope)` |
| One document | `GET /{id}` | readable |
| Its pages' text (`from`, `to`) | `GET /{id}/pages` | readable |
| Its file (inline, or `?download=true`) | `GET /{id}/file` | readable |
| Read it again | `POST /{id}/reindex` | `MayManageAsync(scope)` |
| Change its name or class | `PUT /{id}` | `MayManageAsync(scope)` |
| Delete it | `DELETE /{id}` | `MayManageAsync(scope)`, or the one who added it |
| Search | `POST /search` | readable |
| The site's kinds, limits and classes | `GET /settings` | anyone signed in |

Changing a class is sensitive (`DocumentsHttpOptions.Sensitive`, for a host's step-up). Files are served with `nosniff`,
images and PDFs inline, anything else as a download. Changes are told to the host (`IDocumentsEvents`) for its audit.

### What else a host calls

- `ForgetScopeAsync(scopeId)` for a purged scope: its documents, passages and files go.
- `NamesAccountAsync(accountId)` for an identity package's "account in use", and `ForgetAccountAsync(accountId)` for
  erasure: the account's name goes from what it added (`AddedBy` becomes null).
- `DocumentsSearch` and `DocumentsReader` for an agent's tools: search, a document's pages, the documents in a scope.

### What stays the host's

- the scopes, who may read, add and manage in each, and the names of scopes and people;
- the tables, through `IDocumentsDbContext` and `modelBuilder.ConfigureDocuments(...)` in the host's own context, with
  its schema; its migrations make them, and the `vector` extension (`CREATE EXTENSION IF NOT EXISTS vector`);
- which models read images and make vectors, by alias, and which data classes each may receive (AbpAi's admin);
- an agent's tools, and how it cites.

## Consequences

- A product gets a document library, reading in the background and hybrid search by adding the modules, naming its
  rights, and giving AbpAi an embedding alias and a model that reads images.
- Production's PostgreSQL needs the `vector` extension (pgvector 0.5 or later): the `pgvector/pgvector` images have it;
  Amazon RDS and Azure's flexible server offer it. This repository's tests run on a `pgvector/pgvector` image.
- Search is exact, with no approximate index: right while a person's readable passages are some hundred thousand.
  `cosine_distance` fails on vectors of different sizes, so a query never ranks two models' passages together: the
  model's filter is in the same `WHERE` as the distance.
- The next step is an HNSW index per model, partial and on an expression:
  `CREATE INDEX … USING hnsw ((embedding::vector(N)) vector_cosine_ops) WHERE model = '…'`. The planner uses it only
  when the query casts exactly the same way (`::vector(N)`, not a bare `::vector`), so the query's cast changes with it.
- A Word file's pages are its page breaks, not the pages Word would lay out.

## Open

- Whether a document's class should also decide who reads it (sensitive documents for some of a scope's people), or
  only where its text may go.
