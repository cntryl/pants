using System.Text;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsReplayedDeleteShadowingTests
{
    static readonly byte[] Key = "l1/k003"u8.ToArray();
    const string Absent = "<absent>";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ShouldKeepReplayedDeleteShadowingFlushedValueAfterReopen(bool simulatedCloud, bool shutdown)
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path, simulatedCloud);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await CommitAsync(database, t => t.Put(Key, "old"u8.ToArray()));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await CommitAsync(database, t => t.Delete(Key));
            Assert.Equal(Absent, await ReadAsync(database, Key));
            if (shutdown)
            {
                await database.ShutdownAsync(TimeSpan.FromSeconds(10));
            }
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.Equal(Absent, await ReadAsync(reopened, Key));
        Assert.Empty(await ScanAsync(reopened));
    }

    [Fact]
    public async Task ShouldKeepReplayedDeleteShadowingFlushedValueGivenNewerReplayedPut()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path, simulatedCloud: false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await CommitAsync(database, t => t.Put(Key, "old"u8.ToArray()));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await CommitAsync(database, t => t.Put(Key, "newer"u8.ToArray()));
            await CommitAsync(database, t => t.Delete(Key));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.Equal(Absent, await ReadAsync(reopened, Key));
        Assert.Empty(await ScanAsync(reopened));
    }

    [Fact]
    public async Task ShouldKeepReplayedDeleteShadowingFlushedValueGivenLaterWriteToOtherKey()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path, simulatedCloud: false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await CommitAsync(database, t => t.Put(Key, "old"u8.ToArray()));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await CommitAsync(database, t => t.Delete(Key));
            await CommitAsync(database, t => t.Put("other"u8.ToArray(), "x"u8.ToArray()));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.Equal(Absent, await ReadAsync(reopened, Key));
        Assert.Equal("x", await ReadAsync(reopened, "other"u8.ToArray()));
        Assert.Equal(["other"], await ScanAsync(reopened));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldReplayTransactionDeleteAtomicallyWithItsPuts(bool simulatedCloud)
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path, simulatedCloud);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await CommitAsync(database, t => t.Put(Key, "old"u8.ToArray()));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await CommitAsync(database, t =>
            {
                t.Put("a"u8.ToArray(), "1"u8.ToArray());
                t.Delete(Key);
                t.Put("z"u8.ToArray(), "2"u8.ToArray());
            });
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.Equal("1", await ReadAsync(reopened, "a"u8.ToArray()));
        Assert.Equal(Absent, await ReadAsync(reopened, Key));
        Assert.Equal("2", await ReadAsync(reopened, "z"u8.ToArray()));
        Assert.Equal(["a", "z"], await ScanAsync(reopened));
    }

    [Fact]
    public async Task ShouldKeepReplayedRangeDeleteShadowingFlushedValuesAfterReopen()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path, simulatedCloud: false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await CommitAsync(database, t =>
            {
                t.Put("l1/k001"u8.ToArray(), "a"u8.ToArray());
                t.Put(Key, "b"u8.ToArray());
                t.Put("l1/k009"u8.ToArray(), "c"u8.ToArray());
            });
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await CommitAsync(database, t => t.DeleteRange("l1/k002"u8.ToArray(), "l1/k006"u8.ToArray()));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.Equal(Absent, await ReadAsync(reopened, Key));
        Assert.Equal(["l1/k001", "l1/k009"], await ScanAsync(reopened));
    }

    [Fact]
    public async Task ShouldKeepReplayedDeleteShadowingFlushedValueAcrossRepeatedReopen()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path, simulatedCloud: false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await CommitAsync(database, t => t.Put(Key, "old"u8.ToArray()));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await CommitAsync(database, t => t.Delete(Key));
        }

        await using (var reopened = await PantsDatabase.OpenAsync(options))
        {
            await CommitAsync(reopened, t => t.Put("other"u8.ToArray(), "x"u8.ToArray()));
        }

        await using var final = await PantsDatabase.OpenAsync(options);
        Assert.Equal(Absent, await ReadAsync(final, Key));
        await final.Maintenance.FlushAsync(final.ColumnFamilies.DefaultFamily);
        Assert.Equal(Absent, await ReadAsync(final, Key));
        Assert.Equal(["other"], await ScanAsync(final));
    }

    static PantsOpenOptions CreateOptions(string path, bool simulatedCloud) =>
        simulatedCloud
            ? PantsOpenOptions.SimulatedCloud(path, "pants-tests", "replay/").WithBackgroundCompaction(false)
            : PantsOpenOptions.Local(path).WithBackgroundCompaction(false);

    static async Task CommitAsync(IPantsDatabase database, Action<IPantsTransaction> mutate)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        mutate(transaction);
        await transaction.CommitAsync(
            database.Cloud is null ? PantsWriteOptions.Sync : PantsWriteOptions.CloudStrict);
    }

    static async Task<string> ReadAsync(IPantsDatabase database, byte[] key)
    {
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        return await reader.GetAsync(key) is { } value ? Encoding.UTF8.GetString(value.Span) : Absent;
    }

    static async Task<string[]> ScanAsync(IPantsDatabase database)
    {
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var keys = new List<string>();
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        await foreach (var entry in scan)
        {
            keys.Add(Encoding.UTF8.GetString(entry.Key.Span));
        }

        return [.. keys];
    }
}
