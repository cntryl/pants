using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeTransactionSemanticsHardeningParityTests
{
    static readonly byte[] PrefixEndingInFf = [0x10, 0xff];
    static readonly byte[] ChildOfPrefixEndingInFf = [0x10, 0xff, 0x00];
    static readonly byte[] SiblingOfPrefixEndingInFf = [0x10, 0xfe];

    [Fact]
    public async Task ShouldApplyFlushedRangeTombstoneOutsidePointKeyBounds()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenLocalAsync(directory.Path);
        var family = await database.ColumnFamilies.CreateAsync("range-bounds");
        await using (var seed = await BeginWriteAsync(database, family))
        {
            seed.Put("b"u8.ToArray(), "old"u8.ToArray());
            await seed.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.Maintenance.FlushAsync(family);
        await using (var deleting = await BeginWriteAsync(database, family))
        {
            deleting.Put("m"u8.ToArray(), "unrelated"u8.ToArray());
            deleting.DeleteRange("a"u8.ToArray(), "c"u8.ToArray());
            await deleting.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.Maintenance.FlushAsync(family);
        await using var reader = await BeginReadAsync(database, family);
        Assert.Null(await reader.GetAsync("b"u8.ToArray()));
        Assert.Equal("unrelated", TestBytes.ToText((await reader.GetAsync("m"u8.ToArray()))!.Value));
    }

    [Fact]
    public async Task ShouldApplyLastIntentGivenDuplicateOperationsOnSameKeyWhenCommitting()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenLocalAsync(directory.Path);
        var family = await database.ColumnFamilies.CreateAsync("intents");
        await PutAsync(database, family, "existing", "old");
        await database.Maintenance.FlushAsync(family);

        await using (var duplicate = await BeginWriteAsync(database, family))
        {
            duplicate.Put("new"u8.ToArray(), "first"u8.ToArray());
            duplicate.Put("new"u8.ToArray(), "second"u8.ToArray());
            await duplicate.CommitAsync(PantsWriteOptions.BestEffort);
        }

        await using (var replace = await BeginWriteAsync(database, family))
        {
            replace.Delete("existing"u8.ToArray());
            replace.Insert("existing"u8.ToArray(), "new"u8.ToArray());
            await replace.CommitAsync(PantsWriteOptions.BestEffort);
        }

        Assert.Equal("second", await ReadAsync(database, family, "new"));
        Assert.Equal("new", await ReadAsync(database, family, "existing"));
    }

    [Fact]
    public async Task ShouldHonorPrefixUpperBoundGivenPrefixEndingInFfWhenScanning()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenLocalAsync(directory.Path);
        var family = await database.ColumnFamilies.CreateAsync("binary");
        await using (var writer = await BeginWriteAsync(database, family))
        {
            writer.Put(PrefixEndingInFf, "prefix"u8.ToArray());
            writer.Put(ChildOfPrefixEndingInFf, "child"u8.ToArray());
            writer.Put(SiblingOfPrefixEndingInFf, "sibling"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.BestEffort);
        }

        await using var reader = await BeginReadAsync(database, family);
        await using var scan = await reader.ScanAsync(new PantsScanQuery { Prefix = PrefixEndingInFf });
        var entries = await CollectAsync(scan);

        Assert.Equal(["10FF", "10FF00"], entries.Select(static entry => Convert.ToHexString(entry.Key.Span)));
        Assert.Equal(["prefix", "child"], entries.Select(static entry => TestBytes.ToText(entry.Value)));
    }

    [Fact]
    public async Task ShouldIntersectExplicitEndWithPrefixBoundGivenOverlappingBoundsWhenScanning()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenLocalAsync(directory.Path);
        var family = await database.ColumnFamilies.CreateAsync("prefix-end");
        await using (var seed = await BeginWriteAsync(database, family))
        {
            seed.Put("other"u8.ToArray(), "outside"u8.ToArray());
            seed.Put("pre:a"u8.ToArray(), "a"u8.ToArray());
            seed.Put("pre:b"u8.ToArray(), "b"u8.ToArray());
            seed.Put("pre:c"u8.ToArray(), "c"u8.ToArray());
            await seed.CommitAsync(PantsWriteOptions.BestEffort);
        }

        await using var reader = await BeginReadAsync(database, family);
        await using var scan = await reader.ScanAsync(new PantsScanQuery
        {
            Prefix = "pre:"u8.ToArray(),
            StartInclusive = "pre:a"u8.ToArray(),
            EndExclusive = "pre:c"u8.ToArray()
        });
        var rows = await CollectAsync(scan);

        Assert.Equal(["pre:a:a", "pre:b:b"], rows.Select(static entry =>
            $"{TestBytes.ToText(entry.Key)}:{TestBytes.ToText(entry.Value)}"));
    }

    [Fact]
    public async Task ShouldPersistRangeTombstoneWhenFlushSurvivesRestart()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = await database.ColumnFamilies.CreateAsync("ranges");
            await using (var seed = await BeginWriteAsync(database, family))
            {
                foreach (var key in new[] { "a", "b", "c" })
                {
                    seed.Put(TestBytes.FromString(key), TestBytes.FromString(key));
                }

                await seed.CommitAsync(PantsWriteOptions.Buffered);
            }

            await database.Maintenance.FlushAsync(family);
            await using (var deleting = await BeginWriteAsync(database, family))
            {
                deleting.DeleteRange("b"u8.ToArray(), "d"u8.ToArray());
                await deleting.CommitAsync(PantsWriteOptions.Buffered);
            }

            await database.Maintenance.FlushAsync(family);
            await database.ShutdownAsync(TimeSpan.FromSeconds(5));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var reopenedFamily = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.ColumnFamilies.GetAsync("ranges"));
        await using var reader = await BeginReadAsync(reopened, reopenedFamily);
        Assert.Equal("a", TestBytes.ToText((await reader.GetAsync("a"u8.ToArray()))!.Value));
        Assert.Null(await reader.GetAsync("b"u8.ToArray()));
        Assert.Null(await reader.GetAsync("c"u8.ToArray()));
    }

    [Fact]
    public async Task ShouldPreserveScanLimitGivenDeletedOrFilteredIntentsWhenScanning()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenLocalAsync(directory.Path);
        var family = await database.ColumnFamilies.CreateAsync("scan-limit");
        await using (var seed = await BeginWriteAsync(database, family))
        {
            seed.Put("scan:a"u8.ToArray(), "scan:a"u8.ToArray());
            seed.Put("scan:b"u8.ToArray(), "scan:b"u8.ToArray());
            seed.Put("scan:c"u8.ToArray(), "scan:c"u8.ToArray());
            await seed.CommitAsync(PantsWriteOptions.Sync);
        }

        await using var transaction = await BeginWriteAsync(database, family);
        transaction.Delete("scan:a"u8.ToArray());
        transaction.Put("outside"u8.ToArray(), "filtered"u8.ToArray());
        await using var scan = await transaction.ScanAsync(new PantsScanQuery
        {
            Prefix = "scan:"u8.ToArray(),
            Limit = 2
        });
        var rows = await CollectAsync(scan);

        Assert.Equal(["scan:b", "scan:c"], rows.Select(static entry => TestBytes.ToText(entry.Key)));
    }

    [Fact]
    public async Task ShouldRejectAssertionOnlyCommitGivenDroppedColumnFamily()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenLocalAsync(directory.Path);
        var family = await database.ColumnFamilies.CreateAsync("stale-assertion");
        await using var transaction = await BeginWriteAsync(database, family);
        transaction.AssertValue("must-still-exist"u8.ToArray(), null);
        await database.ColumnFamilies.DropAsync(family);

        var error = await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            transaction.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            database.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly).AsTask());
        Assert.Null(await database.ColumnFamilies.GetAsync("stale-assertion"));
    }

    [Fact]
    public async Task ShouldRejectWriteGivenDroppedColumnFamilyWhenCommitting()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenLocalAsync(directory.Path);
        var family = await database.ColumnFamilies.CreateAsync("stale");
        await using var transaction = await BeginWriteAsync(database, family);
        transaction.Put("must-not-commit"u8.ToArray(), "value"u8.ToArray());
        await database.ColumnFamilies.DropAsync(family);

        var error = await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            transaction.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            database.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly).AsTask());
        Assert.Null(await database.ColumnFamilies.GetAsync("stale"));
    }

    [Fact]
    public async Task ShouldReturnDeletedKeyAbsentGivenDeleteIntentOverSnapshotValueWhenScanning()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenLocalAsync(directory.Path);
        var family = await database.ColumnFamilies.CreateAsync("scan-delete");
        await using (var seed = await BeginWriteAsync(database, family))
        {
            seed.Put("a"u8.ToArray(), "removed"u8.ToArray());
            seed.Put("b"u8.ToArray(), "kept"u8.ToArray());
            await seed.CommitAsync(PantsWriteOptions.Sync);
        }

        await using var transaction = await BeginWriteAsync(database, family);
        transaction.Delete("a"u8.ToArray());
        await using var scan = await transaction.ScanAsync(new PantsScanQuery());
        var rows = await CollectAsync(scan);

        var row = Assert.Single(rows);
        Assert.Equal("b", TestBytes.ToText(row.Key));
        Assert.Equal("kept", TestBytes.ToText(row.Value));
    }

    [Fact]
    public async Task ShouldReturnEachKeyOnceGivenSameKeyInSnapshotAndWriteSetWhenScanning()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenLocalAsync(directory.Path);
        var family = await database.ColumnFamilies.CreateAsync("scan-override");
        await using (var seed = await BeginWriteAsync(database, family))
        {
            seed.Put("a"u8.ToArray(), "old"u8.ToArray());
            seed.Put("b"u8.ToArray(), "stable"u8.ToArray());
            await seed.CommitAsync(PantsWriteOptions.Sync);
        }

        await using var transaction = await BeginWriteAsync(database, family);
        transaction.Put("a"u8.ToArray(), "new"u8.ToArray());
        await using var scan = await transaction.ScanAsync(new PantsScanQuery());
        var rows = await CollectAsync(scan);

        Assert.Equal(["a:new", "b:stable"], rows.Select(static entry =>
            $"{TestBytes.ToText(entry.Key)}:{TestBytes.ToText(entry.Value)}"));
    }

    [Fact]
    public async Task ShouldReturnPointPutGivenDeleteRangeThenPutWhenCommitting()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        var family = await database.ColumnFamilies.CreateAsync("range-put");
        await using (var seed = await BeginWriteAsync(database, family))
        {
            seed.Put("middle"u8.ToArray(), "old"u8.ToArray());
            seed.Put("other"u8.ToArray(), "removed"u8.ToArray());
            await seed.CommitAsync(PantsWriteOptions.Sync);
        }

        await using (var replace = await BeginWriteAsync(database, family))
        {
            replace.DeleteRange("a"u8.ToArray(), "z"u8.ToArray());
            replace.Put("middle"u8.ToArray(), "new"u8.ToArray());
            await replace.CommitAsync(PantsWriteOptions.BestEffort);
        }

        Assert.Equal("new", await ReadAsync(database, family, "middle"));
        Assert.Null(await ReadAsync(database, family, "other"));
    }

    [Fact]
    public async Task ShouldUseTransactionSnapshotTimeForTtlVisibility()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch.AddSeconds(10));
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithBackgroundCompaction(false)
                .WithTtlClock(clock));
        var family = await database.ColumnFamilies.CreateAsync("ttl");
        await using (var writer = await BeginWriteAsync(database, family))
        {
            writer.Put("ttl-key"u8.ToArray(), "value"u8.ToArray(), TimeSpan.FromSeconds(1));
            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await using (var snapshot = await BeginReadAsync(database, family))
        {
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            Assert.Equal("value", TestBytes.ToText((await snapshot.GetAsync("ttl-key"u8.ToArray()))!.Value));
        }

        await using var current = await BeginReadAsync(database, family);
        Assert.Null(await current.GetAsync("ttl-key"u8.ToArray()));
    }

    static ValueTask<IPantsDatabase> OpenLocalAsync(string path) =>
        PantsDatabase.OpenAsync(PantsOpenOptions.Local(path).WithBackgroundCompaction(false));

    static ValueTask<IPantsTransaction> BeginWriteAsync(
        IPantsDatabase database,
        IPantsColumnFamily family) =>
        database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite);

    static ValueTask<IPantsTransaction> BeginReadAsync(
        IPantsDatabase database,
        IPantsColumnFamily family) =>
        database.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly);

    static async Task PutAsync(IPantsDatabase database, IPantsColumnFamily family, string key, string value)
    {
        await using var transaction = await BeginWriteAsync(database, family);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static async Task<string?> ReadAsync(IPantsDatabase database, IPantsColumnFamily family, string key)
    {
        await using var reader = await BeginReadAsync(database, family);
        var value = await reader.GetAsync(TestBytes.FromString(key));
        return value is { } present ? TestBytes.ToText(present) : null;
    }

    static async Task<List<PantsEntry>> CollectAsync(IPantsScan scan)
    {
        var entries = new List<PantsEntry>();
        await foreach (var entry in scan)
        {
            entries.Add(entry);
        }

        Assert.True(scan.IsExhausted);
        Assert.False(scan.IsFailed);
        return entries;
    }
}
