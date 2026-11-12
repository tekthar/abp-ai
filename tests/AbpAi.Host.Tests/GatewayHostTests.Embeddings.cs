using Microsoft.EntityFrameworkCore;

namespace AbpAi.Host.Tests;

/// <summary>
/// Embeddings (0.5.0) through the gateway, as an agent's memory uses them: an alias leads to a model that makes them,
/// the data rules and limits hold, and each call is on the ledger as any call is.
/// </summary>
public sealed partial class GatewayHostTests
{
    [Fact]
    public async Task Texts_are_embedded_through_an_alias_with_the_data_rules_the_limits_and_the_ledger()
    {
        AiAdminService admin = Get<AiAdminService>();
        // A model that makes embeddings beside one that doesn't, on a connection that may receive only public and internal data.
        AiConnectionInfo vectors = await admin.CreateConnectionAsync(SiteAdmin, null, new AiConnectionInput("vectors", "demo", ["vector-small", "chat-small"],
            [AiDataClasses.Public, AiDataClasses.Internal])
        {
            InputPrice = 100,
            ModelSettings = new Dictionary<string, AiModelSettings> { ["vector-small"] = new() { Embeds = true }, ["chat-small"] = new() { Embeds = false } },
        }, default);
        await admin.SetAliasAsync(SiteAdmin, null, "embed", [new AiAliasTarget(vectors.Id, "vector-small")], default);
        IAi ai = Get<IAi>();

        AiEmbedding made = await ai.EmbedAsync(new AiEmbedRequest("embed", ["The March invoice is overdue", "Backups run on Sunday"])
        {
            AccountId = Agent, Purpose = "agent:ask:memory", DataClass = AiDataClasses.Internal, About = new AiSubject("episode", "e1"), Dimensions = 64,
        });
        Assert.Equal((2, 64, "vector-small", "vectors"), (made.Vectors.Count, made.Dimensions, made.Model, made.Connection));
        AiUsageRecord record = await Db(db => db.AiUsage.SingleAsync(r => r.Id == made.UsageId));
        Assert.Equal((AiUsageStates.Done, "agent:ask:memory", "episode", AiDataClasses.Internal, 0), (record.State, record.Purpose, record.AboutKind, record.DataClass,
            record.OutputTokens));
        Assert.True(record.InputTokens > 0 && record.Cost > 0, $"{record.InputTokens} tokens, {record.Cost}");

        // A model that doesn't make them isn't chosen; nor is a connection that may not receive the texts' class.
        AiRefusedException noModel = await Assert.ThrowsAsync<AiRefusedException>(() => ai.EmbedAsync(new AiEmbedRequest("chat-small", ["x"]) { DataClass = AiDataClasses.Public }));
        Assert.Equal(AiRefusalCodes.NoModel, noModel.Code);
        AiRefusedException rows = await Assert.ThrowsAsync<AiRefusedException>(() => ai.EmbedAsync(new AiEmbedRequest("embed", ["x"]) { DataClass = AiDataClasses.Rows }));
        Assert.Equal(AiRefusalCodes.DataNotAllowed, rows.Code);

        // A provider that doesn't make them is refused before anything leaves.
        AiConnectionInfo claude = await admin.CreateConnectionAsync(SiteAdmin, null, new AiConnectionInput("claude", "anthropic", ["claude-x"], Every) { ApiKey = "sk-ant-x" }, default);
        AiRefusedException cant = await Assert.ThrowsAsync<AiRefusedException>(() => ai.EmbedAsync(new AiEmbedRequest("claude-x", ["x"])));
        Assert.Equal(AiRefusalCodes.NoModel, cant.Code);
        Assert.Contains("doesn't make embeddings", cant.Message);

        // The agent's own token limit counts them.
        await admin.SetAccountLimitAsync(SiteAdmin, Agent, new AiLimitInput(AiLimitKinds.Account, AiPeriods.Day, record.InputTokens + 1) { Measure = AiLimitMeasures.Tokens }, default);
        AiRefusedException over = await Assert.ThrowsAsync<AiRefusedException>(() => ai.EmbedAsync(new AiEmbedRequest("embed", ["The March invoice is overdue", "and more"])
        {
            AccountId = Agent, DataClass = AiDataClasses.Internal,
        }));
        Assert.Equal((AiRefusalCodes.OverLimit, AiLimitMeasures.Tokens), (over.Code, over.Limit!.Measure));

        // Refusals are on the ledger with why, at no cost.
        Assert.Equal(4, await Db(db => db.AiUsage.CountAsync(r => r.State == AiUsageStates.Refused && r.Cost == 0)));

        // A vector column is sized once: a model that can't give the size asked for is refused, and one that can answers.
        await admin.CreateConnectionAsync(SiteAdmin, null, new AiConnectionInput("fixed", FixedSizeEmbedder.TheKind, ["fixed-8"], Every), default);
        AiRefusedException size = await Assert.ThrowsAsync<AiRefusedException>(() => ai.EmbedAsync(new AiEmbedRequest("fixed-8", ["x"]) { Dimensions = 16 }));
        Assert.Equal(AiRefusalCodes.Provider, size.Code);
        Assert.Contains("not the 16 asked for", size.Message);
        Assert.Equal(8, (await ai.EmbedAsync(new AiEmbedRequest("fixed-8", ["x"]) { Dimensions = 8 })).Dimensions);
    }
}
