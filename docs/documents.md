# Documents: setting them up in a host

`AbpDocuments` keeps the documents people add, reads them in the background and searches them by meaning and by words
together, among only what each person may read. `AbpDocuments.HttpApi` serves it over HTTP. Why it's built this way:
[ADR 0003](adr/0003-documents.md).

## 1. The modules and the tables

```csharp
[DependsOn(typeof(AbpDocumentsHttpApiModule))] // brings AbpDocumentsModule, AbpAi, AbpCluster and AbpCluster.Storage
public class MyModule : AbpModule { }

[ReplaceDbContext(typeof(IDocumentsDbContext))]
public class MyDbContext : AbpDbContext<MyDbContext>, IDocumentsDbContext
{
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentPage> DocumentPages => Set<DocumentPage>();
    public DbSet<DocumentPassage> DocumentPassages => Set<DocumentPassage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ConfigureDocuments(); // schema "documents"; or ConfigureDocuments("my_schema", new DocumentsTables(...))
    }
}
```

Then `dotnet ef migrations add Documents`. The model asks for the `vector` extension, so the migration begins with
`CREATE EXTENSION IF NOT EXISTS vector`. The account that runs migrations must be allowed to make it (on Amazon RDS and
Azure's flexible server, the extension must be allowed first; the `pgvector/pgvector` images have it).

Vectors are kept as `real[]` and compared with pgvector's `cosine_distance` through `DocumentsSql.CosineDistance`, so
nothing more is set on Npgsql (no `UseVector()`).

## 2. Who reads what

Give `IDocumentsRights` (registered after the module's, which lets no one do anything):

```csharp
public sealed class MyDocumentsRights : IDocumentsRights
{
    public async Task<DocumentsReach> ReadableAsync(Guid accountId, CancellationToken ct) =>
        new(AllScopes: false, Scopes: await ProjectsTheyMayReadAsync(accountId, ct), Site: true);

    public Task<bool> MayAddAsync(Guid accountId, Guid? scopeId, CancellationToken ct) => ...;
    public Task<bool> MayManageAsync(Guid accountId, Guid? scopeId, CancellationToken ct) => ...;
}
```

`ReadableAsync` is asked on every call, for the account it acts for, and goes into the SQL. Rules beyond scopes go in an
`IDocumentsFilter`, which narrows the documents' `IQueryable` and gets your own context, so it can reach your tables:

```csharp
public sealed class RestrictedRecords : IDocumentsFilter
{
    public Task<IQueryable<Document>> FilterAsync(IQueryable<Document> documents, IDocumentsDbContext db, Guid accountId, CancellationToken ct)
    {
        IQueryable<Record> records = ((MyDbContext)db).Records;
        return Task.FromResult(documents.Where(d => d.OwnerKind != "record"
            || records.Any(r => r.Id.ToString() == d.OwnerId && (!r.Restricted || r.Readers.Any(x => x.AccountId == accountId)))));
    }
}
```

The API's caller is the signed-in user's id (`ICurrentUser.Id`); give an `IDocumentsCurrentActor` to read it otherwise.

## 3. The models

Documents use two AbpAi aliases:

| Alias (`Documents:…`) | For | The model must |
| --- | --- | --- |
| `read` (`ReadAlias`) | scanned PDF pages and pictures | take images |
| `embeddings` (`EmbedAlias`) | passages, then questions | make embeddings |

Set them in AbpAi's admin for the site, or for a scope. Each call carries the document's data class, so a connection
that may not receive it isn't chosen:

- no model may read a document's images: it fails, saying so;
- no model may embed it: it's searched by its words only, and its problem says so.

When the embedding alias leads to another model, documents are embedded again from their kept text within
`ModelCheckEvery` (10 minutes).

Usage shows the calls by purpose: `documents:read`, `documents:embed` and `documents:search`.

## 4. Data classes

A document's class is one of AbpAi's (`AbpAi:DataClasses`). By default a document may have any of them except
`aggregates` and `rows`. A host with classes of its own lists them in both places, lowest to highest:

```json
{
  "AbpAi": { "DataClasses": ["public", "internal", "aggregates", "rows", "sensitive", "restricted"] },
  "Documents": { "DataClasses": ["public", "internal", "sensitive", "restricted"], "DefaultDataClass": "internal" }
}
```

An agent's tool should ask search only for the classes its own model may receive (`SearchInput.Classes`): the rest are
counted in `Withheld`, never returned.

## 5. Settings

`Documents` in configuration, or `Configure<DocumentsOptions>`:

| Key | Default | Meaning |
| --- | --- | --- |
| `MaxBytes` | 26214400 (25 MB) | The most bytes a file may have. The API takes up to 64 MB a request. |
| `MaxPages` | 500 | The most pages a document may have. |
| `MaxImagePages` | 100 | The most scanned pages of one document read by a model. |
| `PassageLength`, `PassageOverlap` | 1200, 150 | About how long a passage is, and how much the next repeats. |
| `PollEvery` | 00:00:05 | How often a server looks for documents to read. |
| `RetryWait`, `MaxRetryWait` | 00:01:00, 01:00:00 | The first wait after a limit or a busy model, doubling to the most. |
| `Worker` | true | Whether this server reads documents. |

## 6. A host's own files

A host that keeps files (versioned attachments) names a source and keeps documents up to date:

```csharp
public sealed class Attachments : IDocumentSource
{
    public string Name => "attachments";
    public async Task<DocumentFileContent?> OpenAsync(string key, CancellationToken ct) => ...; // the current version's bytes
}

await documents.PutFromSourceAsync(new SourceDocument("attachments", fileId, version, projectId, name, "internal")
    { OwnerKind = "record", OwnerId = recordId }, ct);           // added, or a new version read again in place
await documents.SetHiddenAsync("attachments", fileId, true, ct); // the record went to the trash
await documents.RemoveFromSourceAsync("attachments", fileId, ct);
```

## 7. Forgetting

- `ForgetScopeAsync(scopeId)`: a purged scope's documents, passages and files.
- `ForgetAccountAsync(accountId)`: for erasure. The documents stay, added by no one.
- `NamesAccountAsync(accountId)`: whether a document still names the account, for an identity package's
  "account in use" check.

## 8. The API

Under `DocumentsHttpOptions.RoutePrefix` (`api/documents`), signed in:

| Route | Does |
| --- | --- |
| `GET settings` | kinds, extensions, the most bytes and pages, the classes |
| `GET ?scopeId=&all=&search=&status=&skip=&take=` | a scope's documents, or all the caller may read |
| `POST` (multipart `file`, `scopeId`, `dataClass`) | adds a file |
| `GET {id}`, `GET {id}/pages?from=&to=`, `GET {id}/file?download=` | a document, its pages' text, its file |
| `PUT {id}` (`name`), `PUT {id}/class` (`dataClass`, sensitive) | renames, changes the class |
| `POST {id}/reindex`, `DELETE {id}` | reads again, deletes |
| `POST search` (`query`, `scopeIds`, `includeSite`, `documentIds`, `take`) | passages, best first |

A document the caller may not read answers 404. Files are served `nosniff`: a PDF or picture inline for the browser's
own viewer, framed only by the site itself; anything else, or with `download`, as an attachment in a sandbox.
