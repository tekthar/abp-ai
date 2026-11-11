using Xunit;

namespace AbpAi.Tests;

/// <summary>A usage row keeps what its columns hold: a long name is recorded shortened, never a failed call.</summary>
public class UsageRecordTests
{
    [Fact]
    public void Long_names_are_cut_to_their_columns()
    {
        string longName = new('n', 150);
        AiUsageRecord record = new(Guid.NewGuid(), DateTime.UtcNow, null, [], null, null, new string('p', 300), longName,
            Guid.NewGuid(), longName, new string('k', 80), new string('m', 250), new string('d', 70), 0m, false);

        Assert.Equal((200, 100, 100, 64, 200, 64),
            (record.Purpose.Length, record.Alias.Length, record.Connection.Length, record.Provider.Length, record.Model.Length, record.DataClass.Length));
    }

    [Fact]
    public void Short_names_are_kept_as_they_are() =>
        Assert.Equal("fast", AiUsageRecord.Cut("fast", 100));
}
