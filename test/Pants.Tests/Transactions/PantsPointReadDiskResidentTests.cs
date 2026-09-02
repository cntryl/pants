namespace Cntryl.Pants.Tests.Transactions;

/// <summary>
/// Slice 2 (issue #219) acceptance coverage: a point read's returned value — not only
/// diagnostics — must be resolved from SST blocks once the writing memtable generation has
/// been released from <c>RuntimeState.FamilyData</c> (see
/// <c>RuntimeState.ReleaseFlushedGeneration</c> and <c>TransactionInstance.ReadVisibleValue</c>).
/// </summary>
public sealed class PantsPointReadDiskResidentTests
{
    [Fact]
    public async Task ShouldResolveAHitFromAnSstAfterItsMemtableGenerationIsReleased()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await PutAsync(database, "present", "flushed-value");

        await database.FlushAsync(database.DefaultColumnFamily);

        Assert.Equal("flushed-value", await ReadAsync(database, "present"));
    }

    [Fact]
    public async Task ShouldReturnNullForAKeyThatWasNeverWritten()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await PutAsync(database, "present", "value");
        await database.FlushAsync(database.DefaultColumnFamily);

        Assert.Null(await ReadAsync(database, "absent"));
    }

    [Fact]
    public async Task ShouldPreferTheNewerLevelWhenTheSameKeyExistsInAnOlderSstToo()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await PutAsync(database, "key", "old-value");
        await database.FlushAsync(database.DefaultColumnFamily);
        await PutAsync(database, "key", "new-value");
        await database.FlushAsync(database.DefaultColumnFamily);

        Assert.Equal("new-value", await ReadAsync(database, "key"));
    }

    [Fact]
    public async Task ShouldReadTheNewestVersionWhenOneKeysVersionsSpanSstBlocks()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        for (var version = 1; version <= 3; version++)
        {
            await PutAsync(database, "large-key", new string((char)('0' + version), 40 * 1_024));
        }

        await database.FlushAsync(database.DefaultColumnFamily);

        Assert.Equal(new string('3', 40 * 1_024), await ReadAsync(database, "large-key"));
    }

    [Fact]
    public async Task ShouldNotResurrectAnOlderValueWhenScanningDuplicateSstVersions()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await PutAsync(database, "deleted", "old-value");
        await DeleteAsync(database, "deleted");
        await database.FlushAsync(database.DefaultColumnFamily);

        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await transaction.ScanAsync(new PantsScanQuery());
        var entries = new List<PantsEntry>();
        await foreach (var entry in scan)
        {
            entries.Add(entry);
        }

        Assert.Empty(entries);
    }

    [Fact]
    public async Task ShouldPreferAnActiveMemtableWriteOverAnOlderFlushedSstValue()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await PutAsync(database, "key", "old-value");
        await database.FlushAsync(database.DefaultColumnFamily);
        await PutAsync(database, "key", "new-in-memory-value");

        Assert.Equal("new-in-memory-value", await ReadAsync(database, "key"));
    }

    [Fact]
    public async Task ShouldTreatAPointDeleteAfterFlushAsAbsent()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await PutAsync(database, "key", "value");
        await database.FlushAsync(database.DefaultColumnFamily);

        await DeleteAsync(database, "key");

        Assert.Null(await ReadAsync(database, "key"));
    }

    [Fact]
    public async Task ShouldTreatAnExpiredTtlEntryAsAbsentEvenThoughItIsPhysicallyPresent()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using var database = await OpenAsync(directory.Path, clock);
        await PutAsync(database, "key", "value", TimeSpan.FromSeconds(1));
        await database.FlushAsync(database.DefaultColumnFamily);

        clock.UtcNow += TimeSpan.FromSeconds(2);

        Assert.Null(await ReadAsync(database, "key"));
    }

    [Fact]
    public async Task ShouldStillReturnAnUnexpiredTtlEntryAfterFlush()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using var database = await OpenAsync(directory.Path, clock);
        await PutAsync(database, "key", "value", TimeSpan.FromHours(1));
        await database.FlushAsync(database.DefaultColumnFamily);

        clock.UtcNow += TimeSpan.FromSeconds(1);

        Assert.Equal("value", await ReadAsync(database, "key"));
    }

    [Fact]
    public async Task ShouldKeepAnOlderPinnedSnapshotSeeingThePreMutationValue()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await PutAsync(database, "key", "original");
        await database.FlushAsync(database.DefaultColumnFamily);

        await using var olderSnapshot = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        await PutAsync(database, "key", "overwritten");
        await database.FlushAsync(database.DefaultColumnFamily);

        var seenByOlderSnapshot = await olderSnapshot.GetAsync(TestBytes.FromString("key"));
        Assert.Equal("original", TestBytes.ToText(seenByOlderSnapshot!.Value));
        Assert.Equal("overwritten", await ReadAsync(database, "key"));
    }

    static ValueTask<IPantsDatabase> OpenAsync(string path, IPantsClock? clock = null)
    {
        var options = PantsOpenOptions.Local(path).WithBackgroundCompaction(false);
        return PantsDatabase.OpenAsync(clock is null ? options : options.WithTtlClock(clock));
    }

    static async Task PutAsync(
        IPantsDatabase database,
        string key,
        string value,
        TimeSpan? timeToLive = null)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value), timeToLive);
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
    }

    static async Task DeleteAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Delete(TestBytes.FromString(key));
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
    }

    static async Task<string?> ReadAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(TestBytes.FromString(key));
        return value is { } present ? TestBytes.ToText(present) : null;
    }
}
