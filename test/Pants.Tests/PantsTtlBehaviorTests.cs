using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants;

public sealed class PantsTtlBehaviorTests
{
    [Fact]
    public async Task ShouldNotReexposeExpiredKeyGivenClockStepsBackwardWhenReading()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch.AddSeconds(10));
        await using var database = await TtlStorageModeTestHarness.OpenAsync(
            TtlStorageMode.Memory,
            directory.Path,
            clock);
        await TtlStorageModeTestHarness.PutAsync(
            database,
            TtlStorageMode.Memory,
            "key",
            "value",
            TimeSpan.FromSeconds(1));
        clock.UtcNow = DateTimeOffset.UnixEpoch.AddSeconds(11);
        Assert.Null(await TtlStorageModeTestHarness.GetTextAsync(database, "key"));

        clock.UtcNow = DateTimeOffset.UnixEpoch.AddMilliseconds(10_500);
        var value = await TtlStorageModeTestHarness.GetTextAsync(database, "key");

        Assert.Null(value);
    }

    [Fact]
    public async Task ShouldUseOneCommitTimeGivenMultipleTtlPutsWhenCommitting()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch.AddSeconds(20));
        await using var database = await TtlStorageModeTestHarness.OpenAsync(
            TtlStorageMode.Memory,
            directory.Path,
            clock);
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("a"u8.ToArray(), "a"u8.ToArray(), TimeSpan.FromSeconds(1));
            clock.UtcNow = DateTimeOffset.UnixEpoch.AddMilliseconds(20_250);
            transaction.Put("b"u8.ToArray(), "b"u8.ToArray(), TimeSpan.FromSeconds(1));
            clock.UtcNow = DateTimeOffset.UnixEpoch.AddMilliseconds(20_500);
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        clock.UtcNow = DateTimeOffset.UnixEpoch.AddMilliseconds(21_499);
        Assert.Equal("a", await TtlStorageModeTestHarness.GetTextAsync(database, "a"));
        Assert.Equal("b", await TtlStorageModeTestHarness.GetTextAsync(database, "b"));

        clock.UtcNow = DateTimeOffset.UnixEpoch.AddMilliseconds(21_500);

        Assert.Null(await TtlStorageModeTestHarness.GetTextAsync(database, "a"));
        Assert.Null(await TtlStorageModeTestHarness.GetTextAsync(database, "b"));
    }

    [Fact]
    public async Task ShouldKeepPendingTtlVisibleGivenReadYourOwnWriteBeforeCommit()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch.AddSeconds(30));
        await using var database = await TtlStorageModeTestHarness.OpenAsync(
            TtlStorageMode.Memory,
            directory.Path,
            clock);
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray(), TimeSpan.FromSeconds(1));

        clock.UtcNow = DateTimeOffset.UnixEpoch.AddSeconds(40);
        var value = await transaction.GetAsync("key"u8.ToArray());

        Assert.Equal("value", TestBytes.ToText(value!.Value));
    }

    [Fact]
    public async Task ShouldUseFixedSnapshotTimeGivenRangeScanCrossesTtlBoundary()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch.AddSeconds(50));
        await using var database = await TtlStorageModeTestHarness.OpenAsync(
            TtlStorageMode.Memory,
            directory.Path,
            clock);
        await using (var writer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("short"u8.ToArray(), "value"u8.ToArray(), TimeSpan.FromSeconds(1));
            writer.Put("stable"u8.ToArray(), "value"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        clock.UtcNow = DateTimeOffset.UnixEpoch.AddMilliseconds(50_999);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        clock.UtcNow = DateTimeOffset.UnixEpoch.AddMilliseconds(51_001);
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var entries = new List<PantsEntry>();
        await foreach (var entry in scan)
        {
            entries.Add(entry);
        }

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task ShouldMatchExpirationGivenResidentAndSpilledTransactionPaths()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch.AddSeconds(60));
        await using var resident = await TtlStorageModeTestHarness.OpenAsync(
            TtlStorageMode.Memory,
            directory.Path,
            clock);
        var spilledOptions = PantsOpenOptions.Local(directory.Path)
            .WithTtlClock(clock)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(4 * 1024))
            .WithMemtableLimits(1024)
            .WithTransactionMemoryPool(1024);
        await using var spilled = await PantsDatabase.OpenAsync(spilledOptions);
        await TtlStorageModeTestHarness.PutAsync(
            resident,
            TtlStorageMode.Memory,
            "key-000",
            new string('x', 1024),
            TimeSpan.FromSeconds(1));
        await using (var transaction = await spilled.Transactions.BeginAsync(
                         spilled.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            var value = Enumerable.Repeat((byte)'x', 1024).ToArray();
            for (var index = 0; index < 6; index++)
            {
                transaction.Put(
                    TestBytes.FromString($"key-{index:000}"),
                    value,
                    TimeSpan.FromSeconds(1));
            }

            Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        clock.UtcNow = DateTimeOffset.UnixEpoch.AddSeconds(61);

        Assert.Null(await TtlStorageModeTestHarness.GetTextAsync(resident, "key-000"));
        Assert.Null(await TtlStorageModeTestHarness.GetTextAsync(spilled, "key-000"));
    }

    [Fact]
    public async Task ShouldKeepExpirationBeyondTheClockRangeVisibleForResidentAndSpilledTransactions()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.MaxValue.AddMilliseconds(-500));
        await using var resident = await TtlStorageModeTestHarness.OpenAsync(
            TtlStorageMode.Memory,
            directory.Path,
            clock);
        var spilledOptions = PantsOpenOptions.Local(directory.Path)
            .WithTtlClock(clock)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(4 * 1024))
            .WithMemtableLimits(1024)
            .WithTransactionMemoryPool(1024);
        await using var spilled = await PantsDatabase.OpenAsync(spilledOptions);
        await using (var writer = await resident.Transactions.BeginAsync(
                         resident.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("max-ttl"u8.ToArray(), "resident"u8.ToArray(), TimeSpan.FromSeconds(1));
            writer.Put("no-ttl"u8.ToArray(), "resident-stable"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await using (var writer = await spilled.Transactions.BeginAsync(
                         spilled.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put(
                "max-ttl"u8.ToArray(),
                Enumerable.Repeat((byte)'t', 1024).ToArray(),
                TimeSpan.FromSeconds(1));
            writer.Put("no-ttl"u8.ToArray(), "spilled-stable"u8.ToArray());
            for (var index = 0; index < 6; index++)
            {
                writer.Put(
                    TestBytes.FromString($"padding-{index:000}"),
                    Enumerable.Repeat((byte)'p', 1024).ToArray());
            }

            Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));
            await writer.CommitAsync(PantsWriteOptions.Sync);
        }

        clock.UtcNow = DateTimeOffset.MaxValue;

        Assert.Equal("resident", await TtlStorageModeTestHarness.GetTextAsync(resident, "max-ttl"));
        Assert.Equal(new string('t', 1024), await TtlStorageModeTestHarness.GetTextAsync(spilled, "max-ttl"));
        Assert.Equal("resident-stable", await TtlStorageModeTestHarness.GetTextAsync(resident, "no-ttl"));
        Assert.Equal("spilled-stable", await TtlStorageModeTestHarness.GetTextAsync(spilled, "no-ttl"));
    }

    [Fact]
    public async Task ShouldPreserveExpirationBeyondTheClockRangeThroughFlushCompactionAndReopen()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.MaxValue.AddMilliseconds(-500));
        await using (var database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path)
                             .WithTtlClock(clock)
                             .WithBackgroundCompaction(false)))
        {
            for (var index = 0; index < 4; index++)
            {
                await using var writer = await database.Transactions.BeginAsync(
                    database.ColumnFamilies.DefaultFamily,
                    PantsTransactionMode.ReadWrite);
                writer.Put(
                    index == 0 ? "max-ttl"u8.ToArray() : TestBytes.FromString($"padding-{index}"),
                    "value"u8.ToArray(),
                    index == 0 ? TimeSpan.FromSeconds(1) : null);
                if (index == 0)
                {
                    writer.Put("no-ttl"u8.ToArray(), "stable"u8.ToArray());
                }

                await writer.CommitAsync(PantsWriteOptions.Sync);
                await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            }

            await database.Maintenance.CompactAllAsync();
            var persistedTtl = Directory
                .GetFiles(Path.Combine(directory.Path, "sst"), "*.sst")
                .Select(File.ReadAllBytes)
                .Select(SstCodec.Decode)
                .SelectMany(static contents => contents.Entries)
                .Single(static entry => entry.Key.AsSpan().SequenceEqual("max-ttl"u8));
            Assert.Equal(checked((ulong)clock.UtcNow.ToUnixTimeMilliseconds() + 1_000), persistedTtl.Expiration);

            clock.UtcNow = DateTimeOffset.MaxValue;
            Assert.Equal("value", await TtlStorageModeTestHarness.GetTextAsync(database, "max-ttl"));
            Assert.Equal("stable", await TtlStorageModeTestHarness.GetTextAsync(database, "no-ttl"));
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithTtlClock(clock)
                .WithBackgroundCompaction(false));

        Assert.Equal("value", await TtlStorageModeTestHarness.GetTextAsync(reopened, "max-ttl"));
        Assert.Equal("stable", await TtlStorageModeTestHarness.GetTextAsync(reopened, "no-ttl"));
    }

    [Fact]
    public async Task ShouldPreserveRawTtlValueGivenForwardSkewDuringFlushAndCompaction()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch.AddSeconds(1));
        await using (var database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path)
                             .WithTtlClock(clock)
                             .WithBackgroundCompaction(false)))
        {
            for (var index = 0; index < 4; index++)
            {
                await using var writer = await database.Transactions.BeginAsync(
                    database.ColumnFamilies.DefaultFamily,
                    PantsTransactionMode.ReadWrite);
                writer.Put(
                    index == 0 ? "ttl-key"u8.ToArray() : TestBytes.FromString($"padding-{index}"),
                    "value"u8.ToArray(),
                    index == 0 ? TimeSpan.FromSeconds(100) : null);
                await writer.CommitAsync(PantsWriteOptions.Buffered);
                await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            }

            clock.UtcNow = DateTimeOffset.UnixEpoch.AddSeconds(200);
            await database.Maintenance.CompactAllAsync();
        }

        clock.UtcNow = DateTimeOffset.UnixEpoch.AddSeconds(50);
        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithTtlClock(clock));

        Assert.Equal("value", await TtlStorageModeTestHarness.GetTextAsync(reopened, "ttl-key"));
    }

    [Theory]
    [InlineData(TtlStorageMode.Memory)]
    [InlineData(TtlStorageMode.Local)]
    [InlineData(TtlStorageMode.Cloud)]
    public async Task ShouldReturnValueGivenTtlNotElapsedWhenReading(TtlStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using var database = await TtlStorageModeTestHarness.OpenAsync(
            mode,
            directory.Path,
            clock);
        await TtlStorageModeTestHarness.PutAsync(
            database,
            mode,
            "key1",
            "value1",
            TimeSpan.FromHours(1));

        clock.UtcNow = DateTimeOffset.UnixEpoch.AddMinutes(30);

        Assert.Equal("value1", await TtlStorageModeTestHarness.GetTextAsync(database, "key1"));
    }

    [Theory]
    [InlineData(TtlStorageMode.Memory)]
    [InlineData(TtlStorageMode.Local)]
    [InlineData(TtlStorageMode.Cloud)]
    public async Task ShouldReturnNoneGivenTtlElapsedWhenReading(TtlStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using var database = await TtlStorageModeTestHarness.OpenAsync(
            mode,
            directory.Path,
            clock);
        await TtlStorageModeTestHarness.PutAsync(
            database,
            mode,
            "key1",
            "value1",
            TimeSpan.FromSeconds(1));

        clock.UtcNow = DateTimeOffset.UnixEpoch.AddSeconds(1);

        Assert.Null(await TtlStorageModeTestHarness.GetTextAsync(database, "key1"));
    }

    [Theory]
    [InlineData(TtlStorageMode.Memory)]
    [InlineData(TtlStorageMode.Local)]
    [InlineData(TtlStorageMode.Cloud)]
    public async Task ShouldNotExpireKeyGivenZeroTtlWhenZeroMeansInfinite(TtlStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using var database = await TtlStorageModeTestHarness.OpenAsync(
            mode,
            directory.Path,
            clock);
        await TtlStorageModeTestHarness.PutAsync(
            database,
            mode,
            "key1",
            "value1",
            TimeSpan.Zero);

        clock.UtcNow = DateTimeOffset.UnixEpoch.AddYears(100);

        Assert.Equal("value1", await TtlStorageModeTestHarness.GetTextAsync(database, "key1"));
    }

    [Theory]
    [InlineData(TtlStorageMode.Local)]
    [InlineData(TtlStorageMode.Cloud)]
    public Task ShouldPersistTtlMetadataGivenRestartWhenReopening(TtlStorageMode mode) =>
        VerifyRestartTtlBoundaryAsync(mode, flush: false, expireWhileClosed: false);

    [Theory]
    [InlineData(TtlStorageMode.Local)]
    [InlineData(TtlStorageMode.Cloud)]
    public Task ShouldPersistTtlMetadataGivenFlushAndRestartWhenReopening(TtlStorageMode mode) =>
        VerifyRestartTtlBoundaryAsync(mode, flush: true, expireWhileClosed: false);

    [Theory]
    [InlineData(TtlStorageMode.Local)]
    [InlineData(TtlStorageMode.Cloud)]
    public Task ShouldExpireAfterRestartGivenTtlElapsedDuringShutdownWhenReopening(TtlStorageMode mode) =>
        VerifyRestartTtlBoundaryAsync(mode, flush: false, expireWhileClosed: true);

    [Theory]
    [InlineData(TtlStorageMode.Memory)]
    [InlineData(TtlStorageMode.Local)]
    [InlineData(TtlStorageMode.Cloud)]
    public Task ShouldKeepExpiredEntriesInvisibleGivenCompactionWhenTtlExceeded(TtlStorageMode mode) =>
        VerifyCompactionTtlVisibilityAsync(mode, expired: true);

    [Theory]
    [InlineData(TtlStorageMode.Memory)]
    [InlineData(TtlStorageMode.Local)]
    [InlineData(TtlStorageMode.Cloud)]
    public Task ShouldPreserveNonExpiredEntriesGivenCompactionWhenTtlNotExceeded(TtlStorageMode mode) =>
        VerifyCompactionTtlVisibilityAsync(mode, expired: false);

    [Theory]
    [InlineData(TtlStorageMode.Memory)]
    [InlineData(TtlStorageMode.Local)]
    [InlineData(TtlStorageMode.Cloud)]
    public async Task ShouldHandleMixedTtlKeysGivenSomeExpireWhenReading(TtlStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using var database = await TtlStorageModeTestHarness.OpenAsync(
            mode,
            directory.Path,
            clock);
        await TtlStorageModeTestHarness.PutAsync(
            database,
            mode,
            "key1",
            "value1",
            TimeSpan.FromSeconds(1));
        await TtlStorageModeTestHarness.PutAsync(
            database,
            mode,
            "key2",
            "value2",
            TimeSpan.Zero);
        await TtlStorageModeTestHarness.PutAsync(
            database,
            mode,
            "key3",
            "value3",
            TimeSpan.FromHours(1));

        clock.UtcNow = DateTimeOffset.UnixEpoch.AddSeconds(1);

        Assert.Null(await TtlStorageModeTestHarness.GetTextAsync(database, "key1"));
        Assert.Equal("value2", await TtlStorageModeTestHarness.GetTextAsync(database, "key2"));
        Assert.Equal("value3", await TtlStorageModeTestHarness.GetTextAsync(database, "key3"));
    }

    [Theory]
    [InlineData(TtlStorageMode.Memory)]
    [InlineData(TtlStorageMode.Local)]
    [InlineData(TtlStorageMode.Cloud)]
    public async Task ShouldUpdateTtlGivenOverwriteWithNewTtlWhenWriting(TtlStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using var database = await TtlStorageModeTestHarness.OpenAsync(
            mode,
            directory.Path,
            clock);
        await TtlStorageModeTestHarness.PutAsync(
            database,
            mode,
            "key1",
            "value1",
            TimeSpan.FromSeconds(1));
        clock.UtcNow = DateTimeOffset.UnixEpoch.AddMilliseconds(500);

        await TtlStorageModeTestHarness.PutAsync(
            database,
            mode,
            "key1",
            "value2",
            TimeSpan.FromHours(1));
        clock.UtcNow = DateTimeOffset.UnixEpoch.AddMilliseconds(1_200);

        Assert.Equal("value2", await TtlStorageModeTestHarness.GetTextAsync(database, "key1"));
    }

    [Theory]
    [InlineData(TtlStorageMode.Memory)]
    [InlineData(TtlStorageMode.Local)]
    [InlineData(TtlStorageMode.Cloud)]
    public async Task ShouldExpireKeysCoveredByRangeTombstoneDuringCompaction(TtlStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using var database = await TtlStorageModeTestHarness.OpenAsync(
            mode,
            directory.Path,
            clock);
        await using (var writer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 1; index <= 10; index++)
            {
                writer.Put(
                    TestBytes.FromString($"k{index}"),
                    "ttl-value"u8.ToArray(),
                    TimeSpan.FromSeconds(1));
            }

            await writer.CommitAsync(TtlStorageModeTestHarness.GetWriteOptions(mode));
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        await using (var deleting = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            deleting.DeleteRange("k3"u8.ToArray(), "k8"u8.ToArray());
            await deleting.CommitAsync(TtlStorageModeTestHarness.GetWriteOptions(mode));
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        Assert.Null(await TtlStorageModeTestHarness.GetTextAsync(database, "k5"));
        Assert.Equal("ttl-value", await TtlStorageModeTestHarness.GetTextAsync(database, "k2"));
        clock.UtcNow = DateTimeOffset.UnixEpoch.AddSeconds(1);
        await database.Maintenance.CompactAllAsync();

        foreach (var key in new[] { "k1", "k2", "k3", "k5", "k7", "k8", "k10" })
        {
            Assert.Null(await TtlStorageModeTestHarness.GetTextAsync(database, key));
        }
    }

    [Theory]
    [InlineData(TtlStorageMode.Memory)]
    [InlineData(TtlStorageMode.Local)]
    [InlineData(TtlStorageMode.Cloud)]
    public async Task ShouldHandleTtlExpiryDuringMultiLevelCompaction(TtlStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using var database = await PantsDatabase.OpenAsync(
            TtlStorageModeTestHarness.CreateOptions(mode, directory.Path, clock).WithBackgroundCompaction(false));
        await WriteTtlGenerationAsync(database, mode, parity: 0, TimeSpan.FromSeconds(1));
        await database.Maintenance.CompactAllAsync();
        var originalL1 = (await database.Diagnostics.GetStorageLayoutAsync()).Levels
            .Where(level => level.Level == 1).SelectMany(level => level.Files).Select(file => file.Name).ToArray();
        if (mode != TtlStorageMode.Memory)
        {
            Assert.NotEmpty(originalL1);
        }
        await WriteTtlGenerationAsync(database, mode, parity: 1, TimeSpan.FromHours(1));
        clock.UtcNow = DateTimeOffset.UnixEpoch.AddSeconds(1);
        await database.Maintenance.CompactAllAsync();
        var filesAfter = (await database.Diagnostics.GetStorageLayoutAsync()).Levels
            .SelectMany(level => level.Files).Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(originalL1, filesAfter.Contains);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 50; index++)
        {
            Assert.Null(await reader.GetAsync(TestBytes.FromString($"ttl-key-{index * 2:0000}")));
            Assert.Equal("long-value", TestBytes.ToText(
                (await reader.GetAsync(TestBytes.FromString($"ttl-key-{index * 2 + 1:0000}")))!.Value));
        }
        var expected = Enumerable.Range(0, 50)
            .Select(index => (Key: $"ttl-key-{index * 2 + 1:0000}", Value: "long-value")).ToArray();
        Assert.Equal(expected, await ReadRowsAsync(reader));
    }

    [Theory]
    [InlineData(TtlStorageMode.Memory)]
    [InlineData(TtlStorageMode.Local)]
    [InlineData(TtlStorageMode.Cloud)]
    public async Task ShouldNotExposeTtlExpiredKeyCoveredByRangeTombstone(TtlStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using var database = await TtlStorageModeTestHarness.OpenAsync(
            mode,
            directory.Path,
            clock);
        await TtlStorageModeTestHarness.PutAsync(
            database,
            mode,
            "k5",
            "ttl-tombstone-value",
            TimeSpan.FromSeconds(1));
        await using (var deleting = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            deleting.DeleteRange("k1"u8.ToArray(), "k9"u8.ToArray());
            await deleting.CommitAsync(TtlStorageModeTestHarness.GetWriteOptions(mode));
        }

        clock.UtcNow = DateTimeOffset.UnixEpoch.AddSeconds(1);

        Assert.Null(await TtlStorageModeTestHarness.GetTextAsync(database, "k5"));
    }

    static async Task VerifyRestartTtlBoundaryAsync(TtlStorageMode mode, bool flush, bool expireWhileClosed)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch.AddSeconds(10));
        var options = TtlStorageModeTestHarness.CreateOptions(mode, directory.Path, clock)
            .WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = await database.ColumnFamilies.CreateAsync("expiry");
            await using (var writer = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite))
            {
                writer.Put("expiring"u8.ToArray(), "value"u8.ToArray(), TimeSpan.FromSeconds(1));
                writer.Put("stable"u8.ToArray(), "control"u8.ToArray());
                await writer.CommitAsync(mode == TtlStorageMode.Cloud ? PantsWriteOptions.CloudStrict : PantsWriteOptions.Sync);
            }
            if (flush)
            {
                await database.Maintenance.FlushAsync(family);
                Assert.NotEmpty((await database.Diagnostics.GetStorageLayoutAsync()).Levels.SelectMany(level => level.Files));
            }
        }
        clock.UtcNow = DateTimeOffset.UnixEpoch.AddMilliseconds(expireWhileClosed ? 11_000 : 10_999);
        await using var reopened = await PantsDatabase.OpenAsync(options);
        var reopenedFamily = await reopened.ColumnFamilies.GetAsync("expiry");
        Assert.NotNull(reopenedFamily);
        await using (var reader = await reopened.Transactions.BeginAsync(reopenedFamily, PantsTransactionMode.ReadOnly))
        {
            var value = await reader.GetAsync("expiring"u8.ToArray());
            Assert.Equal(expireWhileClosed ? null : "value", value is null ? null : TestBytes.ToText(value.Value));
            Assert.Equal("control", TestBytes.ToText((await reader.GetAsync("stable"u8.ToArray()))!.Value));
        }
        clock.UtcNow = DateTimeOffset.UnixEpoch.AddSeconds(11);
        await using var expired = await reopened.Transactions.BeginAsync(reopenedFamily, PantsTransactionMode.ReadOnly);
        Assert.Null(await expired.GetAsync("expiring"u8.ToArray()));
        Assert.Equal("control", TestBytes.ToText((await expired.GetAsync("stable"u8.ToArray()))!.Value));
    }

    static async Task VerifyCompactionTtlVisibilityAsync(TtlStorageMode mode, bool expired)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using var database = await PantsDatabase.OpenAsync(
            TtlStorageModeTestHarness.CreateOptions(mode, directory.Path, clock).WithBackgroundCompaction(false));
        for (var generation = 0; generation < 3; generation++)
        {
            await using var writer = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
            writer.Put(TestBytes.FromString($"filler-{generation}"), TestBytes.FromString($"value-{generation}"));
            writer.Put("shared-key"u8.ToArray(), TestBytes.FromString($"latest-{generation}"));
            await writer.CommitAsync(TtlStorageModeTestHarness.GetWriteOptions(mode));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }
        await TtlStorageModeTestHarness.PutAsync(database, mode, "key1", "value1", TimeSpan.FromSeconds(1));
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var original = (await database.Diagnostics.GetStorageLayoutAsync()).Levels
            .SelectMany(level => level.Files).Select(file => file.Name).ToArray();
        if (mode != TtlStorageMode.Memory)
        {
            Assert.Equal(4, original.Length);
        }
        if (expired)
        {
            clock.UtcNow = DateTimeOffset.UnixEpoch.AddSeconds(1);
        }
        await database.Maintenance.CompactAllAsync();
        var filesAfter = (await database.Diagnostics.GetStorageLayoutAsync()).Levels
            .SelectMany(level => level.Files).Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain(original, filesAfter.Contains);
        Assert.Equal(expired ? null : "value1", await TtlStorageModeTestHarness.GetTextAsync(database, "key1"));
        for (var index = 0; index < 3; index++)
        {
            Assert.Equal($"value-{index}", await TtlStorageModeTestHarness.GetTextAsync(database, $"filler-{index}"));
        }
        Assert.Equal("latest-2", await TtlStorageModeTestHarness.GetTextAsync(database, "shared-key"));
    }

    static async Task WriteTtlGenerationAsync(IPantsDatabase database, TtlStorageMode mode, int parity, TimeSpan ttl)
    {
        await using var writer = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        for (var index = 0; index < 50; index++)
        {
            writer.Put(TestBytes.FromString($"ttl-key-{index * 2 + parity:0000}"),
                TestBytes.FromString(parity == 0 ? "short-value" : "long-value"), ttl);
        }
        await writer.CommitAsync(TtlStorageModeTestHarness.GetWriteOptions(mode));
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }

    static async Task<(string Key, string Value)[]> ReadRowsAsync(IPantsTransaction reader)
    {
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var rows = new List<(string Key, string Value)>();
        await foreach (var entry in scan)
        {
            rows.Add((TestBytes.ToText(entry.Key), TestBytes.ToText(entry.Value)));
        }
        return rows.ToArray();
    }
}
