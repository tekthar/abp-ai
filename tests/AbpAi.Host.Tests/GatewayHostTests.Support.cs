using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Volo.Abp;
using Volo.Abp.Domain.Entities;

namespace AbpAi.Host.Tests;

/// <summary>
/// 0.4.0's: each connection's timeouts (AI6), a connection's test (AI7), the owner's export of calls for support (AI8),
/// and what moving the first products onto 0.3.0 found: refusals kept on the ledger, a limit's admins told it's reached,
/// an agent's admins setting its own limits, and cached tokens counted.
/// </summary>
public sealed partial class GatewayHostTests
{
    private static readonly IReadOnlyList<string> Every = [.. AiDataClasses.Default];
    private static readonly AiActor SalesAdmin = new(Guid.Parse("00000000-0000-0000-0000-0000000000e1"));
    private static readonly Guid Agent = Guid.Parse("30000000-0000-0000-0000-000000000001"); // Finance's admin administers it

    // AI6 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_provider_that_stops_sending_mid_stream_ends_the_call_within_the_idle_timeout()
    {
        // A thousand per million tokens in and out.
        await Get<AiAdminService>().CreateConnectionAsync(SiteAdmin, null, new AiConnectionInput("stalls", SlowProvider.TheKind, ["stall"], Every)
        {
            InputPrice = 1000, OutputPrice = 1000,
        }, default);

        Stopwatch watch = Stopwatch.StartNew();
        List<string> seen = [];
        AiRefusedException refused = await Assert.ThrowsAsync<AiRefusedException>(async () =>
        {
            await foreach (AiStreamEvent e in Get<IAi>().StreamAsync(new AiRequest("stall", AiMessages.User("Hello")) { AccountId = Ben, MaxTokens = 20 }))
            {
                seen.Add(e.Type);
            }
        });

        // The site waits two seconds (in these tests) for the next part.
        Assert.Equal((AiRefusalCodes.Timeout, AiTimeouts.Idle), (refused.Code, refused.TimedOut));
        Assert.Contains("idle timeout", refused.Message);
        Assert.Equal("message_start", Assert.Single(seen));
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(20));
        // It had begun: it costs what it used (its start counted 10 tokens in and 1 out), and the limits count that.
        AiUsageRecord record = await Db(db => db.AiUsage.SingleAsync(r => r.Model == "stall"));
        Assert.Equal((AiUsageStates.Failed, AiRefusalCodes.Timeout, 10, 1, 0.011m), (record.State, record.Code, record.InputTokens, record.OutputTokens, record.Cost));
        await Get<AiAdminService>().SetLimitAsync(SiteAdmin, null, new AiLimitInput(AiLimitKinds.Total, AiPeriods.Day, 0.025m), default);
        AiRefusedException over = await Assert.ThrowsAsync<AiRefusedException>(() => Ask(Pipeline, Cy));
        Assert.Equal((AiRefusalCodes.OverLimit, 0.011m), (over.Code, over.Limit!.Spent));
    }

    [Fact]
    public async Task A_local_model_allowed_longer_to_its_first_token_isnt_cut_off_at_the_sites_default()
    {
        AiAdminService admin = Get<AiAdminService>();
        AiConnectionInput slow = new("local-slow", SlowProvider.TheKind, ["first-3"], Every) { IdleTimeoutSeconds = 10 };
        AiConnectionInfo connection = await admin.CreateConnectionAsync(SiteAdmin, null, slow, default);

        // Three seconds to its first token, where the site waits two: its own ten let it answer.
        Assert.Equal("At last.", (await StreamedAsync("first-3")).Text);

        // Back on the site's: cut off, streamed.
        await admin.UpdateConnectionAsync(SiteAdmin, connection.Id, slow with { IdleTimeoutSeconds = 0 }, default);
        AiRefusedException idle = await Assert.ThrowsAsync<AiRefusedException>(() => StreamedAsync("first-3"));
        Assert.Equal(AiTimeouts.Idle, idle.TimedOut);

        // A call that isn't streamed gets its reply whole: only its total timeout applies to it.
        Assert.Equal("At last.", (await Get<IAi>().CompleteAsync(new AiRequest("first-3", AiMessages.User("Hi")) { AccountId = Ben })).Text);
        await admin.UpdateConnectionAsync(SiteAdmin, connection.Id, slow with { IdleTimeoutSeconds = null, TotalTimeoutSeconds = 1 }, default);
        AiRefusedException total = await Assert.ThrowsAsync<AiRefusedException>(() => Get<IAi>().CompleteAsync(new AiRequest("first-3", AiMessages.User("Hi"))));
        Assert.Equal((AiRefusalCodes.Timeout, AiTimeouts.Total), (total.Code, total.TimedOut));

        // A null keeps what's set; a 0 went back to the site's.
        AiConnectionInfo read = (await admin.ListConnectionsAsync(SiteAdmin, null, default)).Single(c => c.Id == connection.Id);
        Assert.Equal(((int?)null, (int?)1), (read.IdleTimeoutSeconds, read.TotalTimeoutSeconds));
        await Assert.ThrowsAsync<UserFriendlyException>(() => admin.UpdateConnectionAsync(SiteAdmin, connection.Id, slow with { IdleTimeoutSeconds = 7200 }, default));
    }

    // AI7 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_connections_test_says_why_in_the_providers_own_words_and_never_shows_the_key()
    {
        AiAdminService admin = Get<AiAdminService>();
        AiConnectionInput fussy = new("fussy", FussyProvider.TheKind, ["fussy-1", "fussy-2"], Every) { ApiKey = "sk-wrong-1234567", Test = true };

        // A wrong key, tested as it's saved.
        AiConnectionInfo saved = await admin.CreateConnectionAsync(SiteAdmin, null, fussy, default);
        AiConnectionTest badKey = saved.Test!;
        Assert.Equal((false, (int?)401, "fussy-1"), (badKey.Ok, badKey.Status, badKey.Model));
        Assert.Contains("invalid x-api-key", badKey.Reason);
        Assert.DoesNotContain("sk-wrong", badKey.Reason);

        // The right key answers; a model it doesn't have says so.
        await admin.UpdateConnectionAsync(SiteAdmin, saved.Id, fussy with { ApiKey = "good-key", Test = false }, default);
        AiConnectionTest ok = await admin.TestConnectionAsync(SiteAdmin, null, saved.Id, null, default);
        Assert.Equal((true, "fussy-1", (int?)null), (ok.Ok, ok.Model, ok.Status));
        AiConnectionTest badModel = await admin.TestConnectionAsync(SiteAdmin, null, saved.Id, "fussy-2", default);
        Assert.Equal((false, (int?)404), (badModel.Ok, badModel.Status));
        Assert.Contains("model: fussy-2", badModel.Reason);

        // An address that can't be reached says that, through the real provider.
        AiConnectionInfo away = await admin.CreateConnectionAsync(SiteAdmin, null, new AiConnectionInput("away", "anthropic", ["claude-x"], Every)
        {
            Endpoint = "http://127.0.0.1:1", ApiKey = "sk-ant-abcdefghijkl", IdleTimeoutSeconds = 30, Test = true,
        }, default);
        Assert.False(away.Test!.Ok);
        Assert.StartsWith("It couldn't be reached", away.Test.Reason);
        Assert.DoesNotContain("abcdefghijkl", away.Test.Reason);

        // Only admins test, and a test isn't a call: nothing is on the ledger.
        await Assert.ThrowsAsync<UserFriendlyException>(() => admin.TestConnectionAsync(new AiActor(Ben), null, saved.Id, null, default));
        Assert.Equal(0, await Db(db => db.AiUsage.CountAsync()));
    }

    // AI8 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_owner_exports_a_days_calls_and_anyone_else_is_refused()
    {
        AiAdminService admin = Get<AiAdminService>();
        AiExportService exports = Get<AiExportService>();
        await admin.SetSiteAsync(SiteAdmin, new AiSiteSettings(false, true, 30, [AiDataClasses.Public], []), default);
        AiReply kept = await Ask(Budget, Ben);
        await admin.SetSiteAsync(SiteAdmin, new AiSiteSettings(false, false, 30, [AiDataClasses.Public], []), default);
        await Ask(Pipeline, Cy);
        await Assert.ThrowsAsync<AiRefusedException>(() => Ask(Budget, Ben, alias: "public", dataClass: AiDataClasses.Rows));

        DateTime day = DateTime.UtcNow.Date;
        AiExportInfo export = await exports.CreateAsync(SiteAdmin, day, day.AddDays(1), null, default);
        Assert.Equal((3, 1, false), (export.Calls, export.Prompts, export.Truncated));
        Assert.InRange(export.ExpiresAt - export.At, TimeSpan.FromDays(6.9), TimeSpan.FromDays(7.1));

        AiExportFile file = await exports.DownloadAsync(SiteAdmin, export.Id, default);
        Assert.EndsWith(".jsonl.gz", file.Name);
        List<JsonElement> lines = Lines(file.Content);
        Assert.Equal(3, lines.Count);
        JsonElement withPrompt = lines.Single(l => l.GetProperty("id").GetGuid() == kept.UsageId);
        Assert.True(withPrompt.GetProperty("prompt_kept").GetBoolean());
        Assert.Equal(JsonValueKind.Array, withPrompt.GetProperty("messages").ValueKind);
        Assert.Equal(AiDataClasses.Aggregates, withPrompt.GetProperty("data_class").GetString());
        JsonElement refused = lines.Single(l => l.GetProperty("state").GetString() == AiUsageStates.Refused);
        Assert.Equal((AiRefusalCodes.DataNotAllowed, false), (refused.GetProperty("code").GetString(), refused.GetProperty("prompt_kept").GetBoolean()));
        Assert.False(refused.TryGetProperty("messages", out _), "a prompt that wasn't kept can't be exported");
        Assert.Equal([AiAuditActions.CallsExported, AiAuditActions.CallsDownloaded], _audit.Entries.Select(e => e.Action));
        Assert.All(_audit.Entries, e => Assert.Equal((SiteAdmin.Id, (Guid?)export.Id), (e.Actor.Id, e.TargetId)));

        // Anyone else is refused.
        await Assert.ThrowsAsync<UserFriendlyException>(() => exports.CreateAsync(FinanceAdmin, day, day.AddDays(1), Finance, default));
        await Assert.ThrowsAsync<UserFriendlyException>(() => exports.DownloadAsync(new AiActor(Ben), export.Id, default));

        // Nothing is exported that the audit log doesn't hold.
        _audit.Broken = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => exports.CreateAsync(SiteAdmin, day, day.AddDays(1), null, default));
        _audit.Broken = false;
        Assert.Single(await exports.ListAsync(SiteAdmin, default));

        // Its file is gone after its days.
        await Db(db => db.AiExports.Where(e => e.Id == export.Id).ExecuteUpdateAsync(u => u.SetProperty(e => e.ExpiresAt, DateTime.UtcNow.AddMinutes(-1))));
        await _factory!.Services.GetServices<IHostedService>().OfType<AiHousekeeping>().Single().RunOnceAsync(default);
        Assert.Equal(0, await Db(db => db.AiExports.CountAsync()));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => exports.DownloadAsync(SiteAdmin, export.Id, default));
    }

    // What moving onto 0.3.0 found ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_limits_refusals_are_kept_on_the_ledger_and_its_admins_hear_once_that_its_reached()
    {
        await Get<AiAdminService>().SetLimitAsync(SiteAdmin, Sales, new AiLimitInput(AiLimitKinds.Total, AiPeriods.Day, 0.05m), default);

        // Each call reserves 0.02: two answer, and the three after them are refused.
        int answered = 0, refusedCalls = 0;
        for (int i = 0; i < 5; i++)
        {
            try
            {
                await Ask(Pipeline, Cy);
                answered++;
            }
            catch (AiRefusedException ex) when (ex.Code == AiRefusalCodes.OverLimit)
            {
                refusedCalls++;
            }
        }

        Assert.Equal((2, 3), (answered, refusedCalls));
        AiLimitNotice reached = Assert.Single(_notices.Reached);
        Assert.Equal(("scope", (Guid?)Sales, AiPeriods.Day), (reached.Limit.Kind, reached.Limit.ScopeId, reached.Limit.Period));

        List<AiUsageRecord> kept = await Db(db => db.AiUsage.Where(r => r.State == AiUsageStates.Refused).ToListAsync());
        Assert.Equal(3, kept.Count);
        Assert.All(kept, r => Assert.Equal((AiRefusalCodes.OverLimit, 0m, (Guid?)Pipeline), (r.Code, r.Cost, r.ScopeId)));

        // Usage counts them apart, and lists them.
        DateTime day = DateTime.UtcNow.Date;
        AiUsageService usage = Get<AiUsageService>();
        AiUsageReport report = await usage.QueryAsync(SiteAdmin, new AiUsageQuery(day, day.AddDays(1), Sales, AiUsageGroups.Purpose), default);
        AiUsageRow ask = Assert.Single(report.Rows);
        Assert.Equal((2, 3, 0), (ask.Calls, ask.Refused, ask.Failed));
        IReadOnlyList<AiCallRow> calls = await usage.RecentAsync(SiteAdmin, new AiUsageQuery(day, day.AddDays(1), Sales) { States = [AiUsageStates.Refused] }, 10, default);
        Assert.Equal(3, calls.Count);
        Assert.All(calls, c => Assert.Equal(("SALES-PIPE", AiRefusalCodes.OverLimit), (c.Scope, c.Code)));
        Assert.Equal(5, (await usage.MineRecentAsync(new AiActor(Cy), day, day.AddDays(1), 50, default)).Count);
    }

    [Fact]
    public async Task An_agents_admins_set_its_own_limits_on_the_site_and_no_one_else_does()
    {
        AiAdminService admin = Get<AiAdminService>();
        AiLimitInfo limit = await admin.SetAccountLimitAsync(FinanceAdmin, Agent, new AiLimitInput(AiLimitKinds.Account, AiPeriods.Day, 0.03m), default);
        Assert.Equal(((Guid?)null, (Guid?)Agent, AiLimitKinds.Account), (limit.ScopeId, limit.AccountId, limit.Kind));
        Assert.Single(await admin.ListAccountLimitsAsync(FinanceAdmin, Agent, default));

        // It counts the agent's calls everywhere: one answers, the next is refused, in Sales as in Finance.
        await Ask(Budget, Agent);
        AiRefusedException refused = await Assert.ThrowsAsync<AiRefusedException>(() => Ask(Pipeline, Agent));
        Assert.Equal(("account", (Guid?)Agent), (refused.Limit!.Kind, refused.Limit.AccountId));

        // Sales' admin may not, nor may the agent's admin set another account's, nor anything but an account's own kinds.
        await Assert.ThrowsAsync<UserFriendlyException>(() => admin.SetAccountLimitAsync(SalesAdmin, Agent, new AiLimitInput(AiLimitKinds.Account, AiPeriods.Day, 1), default));
        await Assert.ThrowsAsync<UserFriendlyException>(() => admin.ListAccountLimitsAsync(FinanceAdmin, Ben, default));
        await Assert.ThrowsAsync<UserFriendlyException>(() => admin.SetAccountLimitAsync(FinanceAdmin, Agent, new AiLimitInput(AiLimitKinds.Total, AiPeriods.Day, 1), default));
        await Assert.ThrowsAsync<UserFriendlyException>(() => admin.DeleteLimitAsync(SalesAdmin, limit.Id, default));

        await admin.DeleteLimitAsync(FinanceAdmin, limit.Id, default);
        Assert.Empty(await admin.ListAccountLimitsAsync(SiteAdmin, Agent, default));
    }

    [Fact]
    public async Task Cached_tokens_are_counted_and_cost_what_the_cache_prices_say()
    {
        // Per million: 1,000 in and 2,000 out; writes unset (1.25 times in), reads at 50.
        await Get<AiAdminService>().CreateConnectionAsync(SiteAdmin, null, new AiConnectionInput("caching", CachingProvider.TheKind, ["cache-1"], Every)
        {
            InputPrice = 1000, OutputPrice = 2000, CacheReadPrice = 50,
        }, default);

        AiReply reply = await Get<IAi>().CompleteAsync(new AiRequest("cache-1", AiMessages.User("Hi")) { AccountId = Ben, Cache = true });

        // (100 × 1,000 + 1,000 × 1,250 + 2,000 × 50 + 50 × 2,000) per million.
        Assert.Equal(1.55m, reply.Usage.Cost);
        Assert.Equal((100, 1000, 2000, 50), (reply.Usage.InputTokens, reply.Usage.CacheWriteTokens, reply.Usage.CacheReadTokens, reply.Usage.OutputTokens));
        Assert.Contains(true, _caching.Cached);
        AiUsageRecord record = await Db(db => db.AiUsage.SingleAsync(r => r.Id == reply.UsageId));
        Assert.Equal((1000, 2000, 1.55m), (record.CacheWriteTokens, record.CacheReadTokens, record.Cost));

        DateTime day = DateTime.UtcNow.Date;
        AiUsageRow row = Assert.Single((await Get<AiUsageService>().QueryAsync(SiteAdmin, new AiUsageQuery(day, day.AddDays(1), null, AiUsageGroups.Model), default)).Rows);
        Assert.Equal((2000L, 1000L), (row.CacheReadTokens, row.CacheWriteTokens));
    }

    private async Task<AiReply> StreamedAsync(string alias)
    {
        AiReply? reply = null;
        await foreach (AiStreamEvent e in Get<IAi>().StreamAsync(new AiRequest(alias, AiMessages.User("Hello")) { AccountId = Ben, MaxTokens = 20 }))
        {
            reply = e.Reply ?? reply;
        }

        return reply!;
    }

    private static List<JsonElement> Lines(byte[] gzipped)
    {
        using GZipStream zip = new(new MemoryStream(gzipped), CompressionMode.Decompress);
        using StreamReader reader = new(zip, Encoding.UTF8);
        return [.. reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone())];
    }
}
