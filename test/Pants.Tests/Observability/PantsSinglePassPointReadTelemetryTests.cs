using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Observability;

public sealed class PantsSinglePassPointReadTelemetryTests
{
    [Fact]
    public async Task ShouldRecordNoSstReadWhenValueIsServedFromMemtable()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await FlushKeyAsync(database, "flushed");
        await PutAsync(database, "in-memory");
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        Assert.NotNull(await reader.GetAsync("in-memory"u8.ToArray()));

        var metrics = await database.Diagnostics.GetReadAmplificationMetricsAsync();
        Assert.Equal(0, metrics.ReadsTotal);
        Assert.Equal(0, metrics.SstsTouchedTotal);
    }

    [Fact]
    public async Task ShouldRecordOneReadPerGetAndTouchOnlyCandidateFiles()
    {
        const int fileCount = 8;
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        for (var index = 0; index < fileCount; index++)
        {
            await FlushKeyAsync(database, $"key-{index:D2}");
        }

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        Assert.NotNull(await reader.GetAsync("key-03"u8.ToArray()));
        Assert.NotNull(await reader.GetAsync("key-05"u8.ToArray()));

        var metrics = await database.Diagnostics.GetReadAmplificationMetricsAsync();
        Assert.Equal(2, metrics.ReadsTotal);
        Assert.Equal(2, metrics.SstsTouchedTotal);
        Assert.Equal(2L * (fileCount - 1), metrics.KeyRangeRejectsTotal);
    }

    static ValueTask<IPantsDatabase> OpenAsync(string path) =>
        PantsDatabase.OpenAsync(PantsOpenOptions.Local(path).WithBackgroundCompaction(false));

    static async Task FlushKeyAsync(IPantsDatabase database, string key)
    {
        await PutAsync(database, key);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }

    static async Task PutAsync(IPantsDatabase database, string key)
    {
        await using var writer = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        writer.Put(TestBytes.FromString(key), TestBytes.FromString("value"));
        await writer.CommitAsync(PantsWriteOptions.Buffered);
    }
}
