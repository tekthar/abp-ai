using System.Text;
using AbpAi;
using AbpAi.Providers;
using Microsoft.EntityFrameworkCore;
using Volo.Abp;
using Volo.Abp.BlobStoring;

namespace AbpDocuments.Tests;

/// <summary>
/// Documents on PostgreSQL with pgvector: added and read in the background, searched by meaning and words among only
/// what a person may read, kept within their classes, embedded again when the model changes, and forgotten.
/// </summary>
public sealed class DocumentsTests : IAsyncLifetime
{
    private readonly DocumentsHost _host = new();

    private static readonly Guid North = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid South = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Manager = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Contractor = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid Outsider = Guid.Parse("00000000-0000-0000-0000-0000000000b3");

    public Task InitializeAsync() => _host.InitializeAsync();

    public Task DisposeAsync() => _host.DisposeAsync();

    private DocumentsService Documents => _host.Get<DocumentsService>();

    private DocumentsSearch Search => _host.Get<DocumentsSearch>();

    private void Reads(Guid who, params Guid[] scopes) => _host.Rights.Readable[who] = new DocumentsReach(false, scopes, false);

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task Documents_are_read_in_the_background_and_found_only_by_those_who_may_read_them()
    {
        await _host.UseDemoModelsAsync();
        DocumentInfo lease = await _host.AddAsync(Manager, North, "lease.pdf", TestFiles.Pdf(
            ("Lease terms", ["The lease of the north warehouse ends on 30 June 2031.", "Rent is reviewed every two years."]),
            ("Payments", ["Rent is paid quarterly, in advance, by bank transfer."])));
        DocumentInfo leave = await _host.AddAsync(Manager, North, "leave.docx", TestFiles.Word(
            "سياسة الإجازات", "يحصل الموظف على ثلاثين يوماً من الإجازة السنوية.",
            "Carrying over", "Up to 5 days of leave carry over to the next year."));
        DocumentInfo south = await _host.AddAsync(Manager, South, "south-lease.txt", Utf8("The south depot's lease ends in 2027."));
        Assert.Equal(DocumentStatuses.Queued, lease.Status);

        await _host.ReadAllAsync();

        // The manager reads both projects; the contractor only the north; the outsider nothing.
        Reads(Manager, North, South);
        Reads(Contractor, North);
        DocumentInfo read = (await Documents.GetAsync(Manager, lease.Id, default))!;
        Assert.Equal((DocumentStatuses.Ready, 2, "embedder", null), (read.Status, read.Pages, await ModelOf(lease.Id), read.Problem));
        Assert.True(read.Passages >= 2);

        SearchResult found = await Search.SearchAsync(Contractor, new SearchInput("When does the warehouse lease end?"), default);
        FoundPassage best = found.Passages[0];
        Assert.Equal(("lease.pdf", 1), (best.DocumentName, best.Page));
        Assert.Contains("30 June 2031", best.Text);
        Assert.NotNull(best.MeaningRank);
        Assert.NotNull(best.WordsRank);
        Assert.DoesNotContain(found.Passages, p => p.DocumentId == south.Id);
        Assert.Contains((await Search.SearchAsync(Manager, new SearchInput("depot lease"), default)).Passages, p => p.DocumentId == south.Id);
        Assert.Empty((await Search.SearchAsync(Outsider, new SearchInput("lease"), default)).Passages);
        Assert.Equal(0, (await Documents.ListAsync(Outsider, new DocumentsQuery { AllScopes = true }, default)).Total);
        Assert.Null(await Documents.GetAsync(Outsider, lease.Id, default));
        Assert.Null(await Documents.PagesAsync(Outsider, lease.Id, 1, 2, default));
        Assert.Null(await Documents.FileAsync(Outsider, lease.Id, default));

        // Arabic, by its words: a different form of the same word (diacritics, hamza) still matches.
        SearchResult arabic = await Search.SearchAsync(Contractor, new SearchInput("كم يوم اجازة سنوية"), default);
        FoundPassage leavePassage = Assert.Single(arabic.Passages, p => p.DocumentId == leave.Id && p.Page == 1);
        Assert.NotNull(leavePassage.WordsRank);

        // Pages, kept within the document's.
        DocumentPages pages = (await Documents.PagesAsync(Contractor, leave.Id, 2, 99, default))!;
        Assert.Equal((2, 2), (pages.From, pages.To));
        Assert.StartsWith("## Carrying over", Assert.Single(pages.Pages).Text);
        DocumentFile file = (await Documents.FileAsync(Contractor, lease.Id, default))!;
        Assert.Equal(("application/pdf", "lease.pdf"), (file.ContentType, file.Name));

        // Every call to a model said what it was for.
        List<string> purposes = await _host.InDbAsync(db => db.AiUsage.Select(u => u.Purpose).Distinct().ToListAsync());
        Assert.Contains(DocumentsPurposes.Embed, purposes);
        Assert.Contains(DocumentsPurposes.Search, purposes);
    }

    [Fact]
    public async Task Only_what_may_be_read_is_ranked_with_the_hosts_own_rule_in_the_same_query()
    {
        await _host.UseDemoModelsAsync();
        Reads(Contractor, North);
        Reads(Manager, North);
        await _host.InDbAsync(async db =>
        {
            db.Records.Add(new TestRecord { Id = "r-1", Restricted = true, Reader = Manager });
            await db.SaveChangesAsync();
        });
        _host.Get<SourceFiles>().Files["att-1"] = Utf8("The settlement with the supplier is 40,000 riyals.");
        _host.Get<SourceFiles>().Files["att-2"] = Utf8("The supplier's settlement meeting is on Monday.");
        await Documents.PutFromSourceAsync(new SourceDocument(SourceFiles.TheName, "att-1", "v1", North, "settlement.txt") { OwnerKind = "record", OwnerId = "r-1" }, default);
        await Documents.PutFromSourceAsync(new SourceDocument(SourceFiles.TheName, "att-2", "v1", North, "meeting.txt") { OwnerKind = "record", OwnerId = "r-2" }, default);
        await _host.ReadAllAsync();

        // The restricted record's attachment is the manager's only: the contractor's search never ranks it.
        SearchResult contractor = await Search.SearchAsync(Contractor, new SearchInput("supplier settlement"), default);
        Assert.Equal(["meeting.txt"], contractor.Passages.Select(p => p.DocumentName).Distinct());
        SearchResult manager = await Search.SearchAsync(Manager, new SearchInput("supplier settlement"), default);
        Assert.Equal(["meeting.txt", "settlement.txt"], manager.Passages.Select(p => p.DocumentName).Distinct().Order());

        // A new version is read again in place; the old passages go.
        _host.Get<SourceFiles>().Files["att-2"] = Utf8("The meeting moved to Thursday.");
        await Documents.PutFromSourceAsync(new SourceDocument(SourceFiles.TheName, "att-2", "v2", North, "meeting.txt") { OwnerKind = "record", OwnerId = "r-2" }, default);
        await _host.ReadAllAsync();
        FoundPassage moved = Assert.Single((await Search.SearchAsync(Contractor, new SearchInput("meeting"), default)).Passages);
        Assert.Contains("Thursday", moved.Text);
        Assert.Equal("v2", await _host.InDbAsync(db => db.DocumentPassages.Where(p => p.Id == moved.PassageId).Select(p => p.SourceVersion).SingleAsync()));

        // Hidden (the record in the trash) and back, without reading again.
        Assert.True(await Documents.SetHiddenAsync(SourceFiles.TheName, "att-2", true, default));
        Assert.Empty((await Search.SearchAsync(Contractor, new SearchInput("meeting"), default)).Passages);
        Assert.True(await Documents.SetHiddenAsync(SourceFiles.TheName, "att-2", false, default));
        Assert.Single((await Search.SearchAsync(Contractor, new SearchInput("meeting"), default)).Passages);
    }

    [Fact]
    public async Task Passages_above_the_classes_asked_for_are_withheld_and_a_model_never_gets_a_class_it_may_not()
    {
        // The demo connection may not receive restricted text: a restricted document is searched by its words only.
        await _host.UseDemoModelsAsync([AiDataClasses.Public, AiDataClasses.Internal, AiDataClasses.Sensitive]);
        Reads(Contractor, North);
        DocumentInfo pay = await _host.AddAsync(Manager, North, "pay.txt", Utf8("Grade A pay is 20,000 a month."), "restricted");
        await _host.AddAsync(Manager, North, "grades.txt", Utf8("Grades run from A to F."), "internal");
        await _host.ReadAllAsync();

        DocumentInfo read = (await Documents.GetAsync(Contractor, pay.Id, default))!;
        Assert.Equal(DocumentStatuses.Ready, read.Status);
        Assert.StartsWith("Searched by its words only", read.Problem);
        Assert.Null(await ModelOf(pay.Id));
        Assert.Empty(await _host.InDbAsync(db => db.AiUsage.Where(u => u.DataClass == "restricted" && u.State == AiUsageStates.Done).ToListAsync()));

        // An agent whose model takes up to internal: the restricted passage is counted, not returned.
        SearchResult agent = await Search.SearchAsync(Contractor, new SearchInput("grade pay") { Classes = [AiDataClasses.Public, AiDataClasses.Internal] }, default);
        Assert.Equal(["grades.txt"], agent.Passages.Select(p => p.DocumentName).Distinct());
        Assert.Equal(1, agent.Withheld);
        SearchResult person = await Search.SearchAsync(Contractor, new SearchInput("grade pay"), default);
        Assert.Contains(person.Passages, p => p.DocumentName == "pay.txt" && p.DataClass == "restricted" && p.MeaningRank is null);

        // A class it may not have is refused.
        await Assert.ThrowsAsync<UserFriendlyException>(() => _host.AddAsync(Manager, North, "x.txt", Utf8("x"), "rows"));
    }

    [Fact]
    public async Task Two_models_of_different_sizes_live_side_by_side_and_a_new_model_embeds_again()
    {
        AiAdminService admin = _host.Get<AiAdminService>();
        AiConnectionInfo sized = await admin.CreateConnectionAsync(DocumentsHost.Admin, null, new AiConnectionInput("sized", SizedEmbedder.TheKind, ["vec-8", "vec-16", "vec-32"],
            [.. AiDataClasses.Default, "restricted"])
        {
            ModelSettings = new Dictionary<string, AiModelSettings> { ["vec-8"] = new() { Embeds = true }, ["vec-16"] = new() { Embeds = true }, ["vec-32"] = new() { Embeds = true } },
        }, default);
        await admin.SetAliasAsync(DocumentsHost.Admin, null, "embeddings", [new AiAliasTarget(sized.Id, "vec-16")], default);
        await admin.SetAliasAsync(DocumentsHost.Admin, North, "embeddings", [new AiAliasTarget(sized.Id, "vec-8")], default);
        Reads(Manager, North, South);
        DocumentInfo north = await _host.AddAsync(Manager, North, "north.txt", Utf8("The north boiler is serviced in March."));
        DocumentInfo south = await _host.AddAsync(Manager, South, "south.txt", Utf8("The south boiler is serviced in May."));
        await _host.ReadAllAsync();
        Assert.Equal(("vec-8", "vec-16"), (await ModelOf(north.Id), await ModelOf(south.Id)));

        // One search, two models: each compared only with its own.
        SearchResult both = await Search.SearchAsync(Manager, new SearchInput("boiler service"), default);
        Assert.Equal(2, both.Passages.Count(p => p.MeaningRank is not null));
        Assert.Empty(both.Notes);

        // The site's alias moves: the south's document is embedded again from its kept text.
        await admin.SetAliasAsync(DocumentsHost.Admin, null, "embeddings", [new AiAliasTarget(sized.Id, "vec-32")], default);
        await ActivatorUtilitiesWorker().CheckModelsAsync(default);
        Assert.Equal(DocumentStatuses.Queued, (await Documents.GetAsync(Manager, south.Id, default))!.Status);
        await _host.ReadAllAsync();
        Assert.Equal(("vec-8", "vec-32"), (await ModelOf(north.Id), await ModelOf(south.Id)));
        Assert.Equal(2, (await Search.SearchAsync(Manager, new SearchInput("boiler service"), default)).Passages.Count(p => p.MeaningRank is not null));
    }

    [Fact]
    public async Task A_limit_makes_a_document_wait_and_a_scanned_page_is_read_by_a_model()
    {
        await _host.UseDemoModelsAsync();
        Reads(Manager, North);
        _host.Get<AiScript>().OnPurpose(DocumentsPurposes.Read, _ => AiMessages.AssistantText("# Delivery note\n\nDelivered: 12 pallets of cement."));
        DocumentInfo scanned = await _host.AddAsync(Manager, North, "delivery.pdf", TestFiles.ScannedPdf());
        DocumentInfo photo = await _host.AddAsync(Manager, North, "receipt.png", TestFiles.Png());
        await _host.ReadAllAsync();

        DocumentPages page = (await Documents.PagesAsync(Manager, scanned.Id, 1, 1, default))!;
        Assert.True(Assert.Single(page.Pages).FromImage);
        Assert.Equal(1, page.Document.PagesFromImages);
        Assert.Contains(await Search.SearchAsync(Manager, new SearchInput("how many pallets of cement"), default) is { } r ? r.Passages : [],
            p => p.DocumentId == scanned.Id && p.Heading == "Delivery note");
        Assert.Equal(DocumentStatuses.Ready, (await Documents.GetAsync(Manager, photo.Id, default))!.Status);
        AiUsageRecord readCall = await _host.InDbAsync(db => db.AiUsage.FirstAsync(u => u.Purpose == DocumentsPurposes.Read));
        Assert.Equal((AiDataClasses.Internal, North, Manager), (readCall.DataClass, readCall.ScopeId, readCall.AccountId));

        // The adder's daily tokens are spent: the next document waits, it doesn't fail.
        await _host.Get<AiAdminService>().SetAccountLimitAsync(DocumentsHost.Admin, Manager,
            new AiLimitInput(AiLimitKinds.Account, AiPeriods.Day, 1) { Measure = AiLimitMeasures.Tokens }, default);
        DocumentInfo later = await _host.AddAsync(Manager, North, "later.txt", Utf8("Something to embed later."));
        await _host.ReadAllAsync();
        DocumentInfo waiting = (await Documents.GetAsync(Manager, later.Id, default))!;
        Assert.Equal(DocumentStatuses.Waiting, waiting.Status);
        Assert.True(waiting.RetryAt > DateTime.UtcNow);
        Assert.NotNull(waiting.Problem);
    }

    [Fact]
    public async Task An_adder_files_at_the_default_class_or_stricter_and_a_slow_reading_fails()
    {
        await _host.UseDemoModelsAsync();
        Reads(Contractor, North);
        // Public lets text go to more models: only a manager may file a document so.
        UserFriendlyException refused = await Assert.ThrowsAsync<UserFriendlyException>(() => _host.AddAsync(Contractor, North, "flyer.txt", Utf8("Open day."), "public"));
        Assert.Equal(DocumentsErrorCodes.Forbidden, refused.Code);
        Assert.Equal("sensitive", (await _host.AddAsync(Contractor, North, "hr.txt", Utf8("Salaries."), "sensitive")).DataClass);
        _host.Rights.Manages[(Manager, North)] = true;
        Assert.Equal("public", (await _host.AddAsync(Manager, North, "flyer.txt", Utf8("Open day."), "public")).DataClass);

        // A reading past its time ends failed, its lock let go.
        _host.Get<Microsoft.Extensions.Options.IOptions<DocumentsOptions>>().Value.ReadTimeout = TimeSpan.FromMilliseconds(1);
        DocumentInfo slow = await _host.AddAsync(Manager, North, "slow.pdf", TestFiles.Pdf(("Terms", ["Long reading."])));
        await _host.ReadAllAsync();
        DocumentInfo failed = (await Documents.GetAsync(Contractor, slow.Id, default))!;
        Assert.Equal(DocumentStatuses.Failed, failed.Status);
        Assert.Contains("took longer than", failed.Problem);
    }

    [Fact]
    public async Task Deleting_changing_and_forgetting()
    {
        await _host.UseDemoModelsAsync();
        Reads(Manager, North);
        Reads(Contractor, North);
        DocumentInfo mine = await _host.AddAsync(Contractor, North, "mine.txt", Utf8("The contractor's own notes."));
        DocumentInfo theirs = await _host.AddAsync(Manager, North, "theirs.txt", Utf8("The manager's own notes."));
        await _host.ReadAllAsync();

        // Whoever added a document may delete it; a scope's manager may change or delete any; others may not.
        await Assert.ThrowsAsync<UserFriendlyException>(() => Documents.DeleteAsync(Contractor, theirs.Id, default));
        await Assert.ThrowsAsync<UserFriendlyException>(() => Documents.UpdateAsync(Contractor, mine.Id, new DocumentChange(Name: "x"), default));
        await Documents.DeleteAsync(Contractor, mine.Id, default);
        Assert.Equal(0, await _host.InDbAsync(db => db.DocumentPassages.CountAsync(p => p.DocumentId == mine.Id)));
        Assert.Equal(0, await _host.InDbAsync(db => db.DocumentPages.CountAsync(p => p.DocumentId == mine.Id)));

        _host.Rights.Manages[(Manager, North)] = true;
        DocumentInfo renamed = await Documents.UpdateAsync(Manager, theirs.Id, new DocumentChange(Name: "notes.txt", DataClass: "sensitive"), default);
        Assert.Equal(("notes.txt", "sensitive", DocumentStatuses.Queued), (renamed.Name, renamed.DataClass, renamed.Status));
        await _host.ReadAllAsync();
        Assert.Equal(DocumentStatuses.Ready, (await Documents.GetAsync(Manager, theirs.Id, default))!.Status);

        // An account in use, then forgotten; a purged scope's documents go with their files.
        Assert.True(await Documents.NamesAccountAsync(Manager, default));
        Assert.Equal(1, await Documents.ForgetAccountAsync(Manager, default));
        Assert.False(await Documents.NamesAccountAsync(Manager, default));
        Assert.Equal(1, await Documents.ForgetScopeAsync(North, default));
        Assert.Equal(0, await _host.InDbAsync(db => db.Documents.CountAsync()));
        Assert.Null(await _host.Get<IBlobContainer<DocumentsStore>>().GetAllBytesOrNullAsync(DocumentsStore.BlobOf(theirs.Id)));
    }

    private DocumentsWorker ActivatorUtilitiesWorker() =>
        Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<DocumentsWorker>(_host.Services);

    private Task<string?> ModelOf(Guid id) => _host.InDbAsync(db => db.Documents.Where(d => d.Id == id).Select(d => d.Model).SingleAsync());
}

/// <summary>A host's own files, read in place by key.</summary>
public sealed class SourceFiles : IDocumentSource
{
    public const string TheName = "attachments";

    public System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Files { get; } = new();

    public string Name => TheName;

    public Task<DocumentFileContent?> OpenAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(Files.TryGetValue(key, out byte[]? bytes) ? new DocumentFileContent(new MemoryStream(bytes)) : null);
}
