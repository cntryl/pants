using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class CurrentMidgeUnsignedSequenceTests
{
    [Theory]
    [InlineData("high-wal")]
    [InlineData("high-sst")]
    public async Task ShouldReadExtendSnapshotFlushCompactAndReopenCurrentMidgeUnsignedHistory(string image)
    {
        using var directory = CopyFixture(image);
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        var openingFloor = WalLedgerStore.LoadExisting(Path.Combine(directory.Path, "wal")).ReservedThrough;
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = database.ColumnFamilies.DefaultFamily;
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(9223372036857921552UL, before.CurrentSequence);
            Assert.Equal(openingFloor, before.CurrentSequence);
            Assert.Equal(openingFloor, before.WalLocalDurableSequence);
            Assert.Equal(0UL, before.WalLastSyncedSequence);
            await using (var snapshot = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly))
            {
                Assert.Equal(9223372036854775827UL,
                    Assert.Single((await database.Diagnostics.GetStorageLayoutAsync()).ActiveSnapshots).Sequence);
                Assert.Equal("midge-codec"u8.ToArray(), (await snapshot.GetAsync("u64-row"u8.ToArray()))?.ToArray());
                Assert.Equal("actual-runtime"u8.ToArray(), (await snapshot.GetAsync("engine-row"u8.ToArray()))?.ToArray());
                await using (var write = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite))
                {
                    write.Put("u64-row"u8.ToArray(), "pants-extended"u8.ToArray());
                    write.DeleteRange("engine-"u8.ToArray(), "engine."u8.ToArray());
                    await write.CommitAsync(PantsWriteOptions.Sync);
                }

                Assert.Equal(openingFloor + 4,
                    (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
                await database.Maintenance.FlushAsync(family);
                await database.Maintenance.CompactAllAsync();
                Assert.Equal("midge-codec"u8.ToArray(), (await snapshot.GetAsync("u64-row"u8.ToArray()))?.ToArray());
                Assert.Equal("actual-runtime"u8.ToArray(), (await snapshot.GetAsync("engine-row"u8.ToArray()))?.ToArray());
                var layout = await database.Diagnostics.GetStorageLayoutAsync();
                Assert.Contains(layout.Levels.SelectMany(static level => level.Files),
                    static file => file.LargestSequence is { } sequence && Convert.ToUInt64(sequence) > long.MaxValue);
            }

            await AssertExtendedRowsAsync(database);
            await database.ShutdownAsync(TestTimeouts.Expected);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertExtendedRowsAsync(reopened);
    }

    [Theory]
    [InlineData("high-wal")]
    [InlineData("high-sst")]
    public async Task ShouldVerifyCurrentMidgeUnsignedHistoryWithoutNarrowingItsWalBoundary(string image)
    {
        using var directory = CopyFixture(image);
        await using (var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)
                         .WithBackgroundCompaction(false)))
        {
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await database.ShutdownAsync(TestTimeouts.Expected);
        }

        var report = await PantsDatabase.VerifyPathAsync(directory.Path);
        Assert.True(report.Authoritative);
        if (report.WalBoundary is { } boundary)
        {
            Assert.True(Convert.ToUInt64(boundary) > long.MaxValue);
        }
    }

    static async Task AssertExtendedRowsAsync(IPantsDatabase database)
    {
        await using var read = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("pants-extended"u8.ToArray(), (await read.GetAsync("u64-row"u8.ToArray()))?.ToArray());
        Assert.Null(await read.GetAsync("engine-row"u8.ToArray()));
        await using var scan = await read.ScanAsync(new PantsScanQuery());
        var rows = new List<PantsEntry>();
        await foreach (var row in scan)
        {
            rows.Add(row);
        }
        var only = Assert.Single(rows);
        Assert.Equal("u64-row"u8.ToArray(), only.Key.ToArray());
        Assert.Equal("pants-extended"u8.ToArray(), only.Value.ToArray());
    }

    static TemporaryDirectory CopyFixture(string image)
    {
        var directory = new TemporaryDirectory();
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Compatibility", "Midge",
            "c684310d9d8ce8ecd1151c47151d4bea98b348cf", "unsigned-sequence-" + image);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(directory.Path, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        return directory;
    }
}
