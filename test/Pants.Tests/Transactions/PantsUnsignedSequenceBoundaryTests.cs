using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Transactions;

public sealed class PantsUnsignedSequenceBoundaryTests
{
    [Fact]
    public async Task ShouldCommitLastLiveMemorySequenceAndRejectAnotherWriteWithoutChangingRows()
    {
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.InMemory(),
            new RuntimeDependencies(initialMemorySequence: ulong.MaxValue - 4));
        await using (var write = await BeginWriteAsync(database))
        {
            write.Put("last"u8.ToArray(), "accepted"u8.ToArray());
            await write.CommitAsync(PantsWriteOptions.Sync);
        }

        var before = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(ulong.MaxValue - 1, before.CurrentSequence);
        await using (var rejected = await BeginWriteAsync(database))
        {
            rejected.Put("overflow"u8.ToArray(), "must-not-appear"u8.ToArray());
            var failure = await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
                rejected.CommitAsync(PantsWriteOptions.Sync).AsTask());
            Assert.Equal(PantsErrorCode.ResourceLimit, failure.Code);
        }

        var after = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(before.CurrentSequence, after.CurrentSequence);
        Assert.Equal(before.WalBytesWrittenTotal, after.WalBytesWrittenTotal);
        Assert.Equal(before.WalPendingWrites, after.WalPendingWrites);
        await using var read = await BeginReadAsync(database);
        Assert.Equal("accepted"u8.ToArray(), (await read.GetAsync("last"u8.ToArray()))?.ToArray());
        Assert.Null(await read.GetAsync("overflow"u8.ToArray()));
        Assert.Equal(new[] { ("last", "accepted") }, await ReadRowsAsync(read));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ShouldRetainUnsignedSnapshotAndRejectOverlappingWriteAfterNewerMutationWasFlushed(
        bool staleRange,
        bool concurrentRangeDelete)
    {
        using var directory = CopyHighSequenceFixture();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            Assert.True((await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence > long.MaxValue);
            await using (var snapshot = await BeginReadAsync(database))
            {
                await using var stale = await BeginWriteAsync(database);
                stale.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
                if (staleRange)
                {
                    stale.DeleteRange("u64-"u8.ToArray(), "u64."u8.ToArray());
                }
                else
                {
                    stale.Put("u64-row"u8.ToArray(), "rejected"u8.ToArray());
                }

                await using (var newer = await BeginWriteAsync(database))
                {
                    if (concurrentRangeDelete)
                    {
                        newer.DeleteRange("u64-"u8.ToArray(), "u64."u8.ToArray());
                    }
                    else
                    {
                        newer.Put("u64-row"u8.ToArray(), "newer"u8.ToArray());
                    }

                    await newer.CommitAsync(PantsWriteOptions.Sync);
                }

                await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
                await database.Maintenance.CompactAllAsync();
                var beforeRejection = await database.Diagnostics.GetRuntimeMetricsAsync();
                Assert.True(beforeRejection.CurrentSequence > long.MaxValue);
                Assert.Equal("midge-codec"u8.ToArray(), (await snapshot.GetAsync("u64-row"u8.ToArray()))?.ToArray());
                Assert.Equal(new[] { ("engine-row", "actual-runtime"), ("u64-row", "midge-codec") },
                    await ReadRowsAsync(snapshot));

                var failure = await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
                    stale.CommitAsync(PantsWriteOptions.Sync).AsTask());
                Assert.Equal(staleRange, failure.IsRangeConflict);
                var afterRejection = await database.Diagnostics.GetRuntimeMetricsAsync();
                Assert.Equal(beforeRejection.CurrentSequence, afterRejection.CurrentSequence);
                Assert.Equal(beforeRejection.WalBytesWrittenTotal, afterRejection.WalBytesWrittenTotal);
            }

            await AssertCurrentRowsAsync(database, concurrentRangeDelete);
            await database.ShutdownAsync(TestTimeouts.Expected);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertCurrentRowsAsync(reopened, concurrentRangeDelete);
    }

    [Fact]
    public async Task ShouldReopenAndWriteUnsignedHistoryWithDroppedFamilyFrontierAtMaximum()
    {
        using var directory = CopyHighSequenceFixture();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var discarded = await database.ColumnFamilies.CreateAsync("discarded");
            await database.ColumnFamilies.DropAsync(discarded);
            await database.ShutdownAsync(TestTimeouts.Expected);
        }

        var manifest = CloudManifestReader.ReadManifest(directory.Path)!;
        var dropped = Assert.Single(manifest.ColumnFamilies);
        Assert.Equal("discarded", dropped.Name);
        Assert.Equal(ulong.MaxValue, manifest.FlushedThrough[dropped.Id]);
        Assert.True(dropped.DropSequence > long.MaxValue);
        await using (var reopened = await PantsDatabase.OpenAsync(options))
        {
            Assert.Null(await reopened.ColumnFamilies.GetAsync("discarded"));
            await using (var write = await BeginWriteAsync(reopened))
            {
                write.Put("after-drop"u8.ToArray(), "accepted"u8.ToArray());
                await write.CommitAsync(PantsWriteOptions.Sync);
            }

            var metrics = await reopened.Diagnostics.GetRuntimeMetricsAsync();
            Assert.InRange(metrics.CurrentSequence, (ulong)long.MaxValue + 1, ulong.MaxValue - 1);
            await reopened.Maintenance.FlushAsync(reopened.ColumnFamilies.DefaultFamily);
            await reopened.ShutdownAsync(TestTimeouts.Expected);
        }

        await using var final = await PantsDatabase.OpenAsync(options);
        Assert.Null(await final.ColumnFamilies.GetAsync("discarded"));
        await using var read = await BeginReadAsync(final);
        Assert.Equal(new[]
        {
            ("after-drop", "accepted"), ("engine-row", "actual-runtime"), ("u64-row", "midge-codec")
        }, await ReadRowsAsync(read));
    }

    static async Task AssertCurrentRowsAsync(IPantsDatabase database, bool deleted)
    {
        await using var read = await BeginReadAsync(database);
        if (deleted)
        {
            Assert.Null(await read.GetAsync("u64-row"u8.ToArray()));
            Assert.Equal(new[] { ("engine-row", "actual-runtime") }, await ReadRowsAsync(read));
        }
        else
        {
            Assert.Equal("newer"u8.ToArray(), (await read.GetAsync("u64-row"u8.ToArray()))?.ToArray());
            Assert.Equal(new[] { ("engine-row", "actual-runtime"), ("u64-row", "newer") },
                await ReadRowsAsync(read));
        }
    }

    static async Task<List<(string Key, string Value)>> ReadRowsAsync(IPantsTransaction transaction)
    {
        await using var scan = await transaction.ScanAsync(new PantsScanQuery());
        var rows = new List<(string Key, string Value)>();
        await foreach (var row in scan)
        {
            rows.Add((TestBytes.ToText(row.Key), TestBytes.ToText(row.Value)));
        }

        return rows;
    }

    static ValueTask<IPantsTransaction> BeginWriteAsync(IPantsDatabase database) =>
        database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);

    static ValueTask<IPantsTransaction> BeginReadAsync(IPantsDatabase database) =>
        database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);

    static TemporaryDirectory CopyHighSequenceFixture()
    {
        var directory = new TemporaryDirectory();
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Compatibility", "Midge",
            "c684310d9d8ce8ecd1151c47151d4bea98b348cf", "unsigned-sequence-high-sst");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(directory.Path, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return directory;
    }
}
