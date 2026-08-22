namespace Pants.Tests;

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
        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("a"u8.ToArray(), "a"u8.ToArray(), TimeSpan.FromSeconds(1));
            transaction.Put("b"u8.ToArray(), "b"u8.ToArray(), TimeSpan.FromSeconds(1));
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        clock.UtcNow = DateTimeOffset.UnixEpoch.AddSeconds(21);

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
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
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
        await using (var writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("short"u8.ToArray(), "value"u8.ToArray(), TimeSpan.FromSeconds(1));
            writer.Put("stable"u8.ToArray(), "value"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        clock.UtcNow = DateTimeOffset.UnixEpoch.AddMilliseconds(50_999);
        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
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
        await using (var transaction = await spilled.BeginTransactionAsync(
                         spilled.DefaultColumnFamily,
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
    public async Task ShouldPreserveMaximumExpirationGivenResidentAndSpilledTransactions()
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
        await using (var writer = await resident.BeginTransactionAsync(
                         resident.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("max-ttl"u8.ToArray(), "resident"u8.ToArray(), TimeSpan.FromSeconds(1));
            writer.Put("no-ttl"u8.ToArray(), "resident-stable"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await using (var writer = await spilled.BeginTransactionAsync(
                         spilled.DefaultColumnFamily,
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

        Assert.Null(await TtlStorageModeTestHarness.GetTextAsync(resident, "max-ttl"));
        Assert.Null(await TtlStorageModeTestHarness.GetTextAsync(spilled, "max-ttl"));
        Assert.Equal("resident-stable", await TtlStorageModeTestHarness.GetTextAsync(resident, "no-ttl"));
        Assert.Equal("spilled-stable", await TtlStorageModeTestHarness.GetTextAsync(spilled, "no-ttl"));
    }

    [Fact]
    public async Task ShouldRoundTripMaximumExpirationValueGivenSstFlushWhenTtlSaturatesToUlongMax()
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
                await using var writer = await database.BeginTransactionAsync(
                    database.DefaultColumnFamily,
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
                await database.FlushAsync(database.DefaultColumnFamily);
            }

            await database.CompactAllAsync();
            var persistedTtl = Directory
                .GetFiles(Path.Combine(directory.Path, "sst"), "*.sst")
                .Select(File.ReadAllBytes)
                .Select(MidgeSstCodec.Decode)
                .SelectMany(static contents => contents.Entries)
                .Single(static entry => entry.Key.AsSpan().SequenceEqual("max-ttl"u8));
            Assert.Equal(ulong.MaxValue, persistedTtl.Expiration);

            clock.UtcNow = DateTimeOffset.MaxValue;
            Assert.Null(await TtlStorageModeTestHarness.GetTextAsync(database, "max-ttl"));
            Assert.Equal("stable", await TtlStorageModeTestHarness.GetTextAsync(database, "no-ttl"));
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithTtlClock(clock)
                .WithBackgroundCompaction(false));

        Assert.Null(await TtlStorageModeTestHarness.GetTextAsync(reopened, "max-ttl"));
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
                await using var writer = await database.BeginTransactionAsync(
                    database.DefaultColumnFamily,
                    PantsTransactionMode.ReadWrite);
                writer.Put(
                    index == 0 ? "ttl-key"u8.ToArray() : TestBytes.FromString($"padding-{index}"),
                    "value"u8.ToArray(),
                    index == 0 ? TimeSpan.FromSeconds(100) : null);
                await writer.CommitAsync(PantsWriteOptions.Buffered);
                await database.FlushAsync(database.DefaultColumnFamily);
            }

            clock.UtcNow = DateTimeOffset.UnixEpoch.AddSeconds(200);
            await database.CompactAllAsync();
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
    public async Task ShouldPersistTtlMetadataGivenRestartWhenReopening(TtlStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using (var database = await TtlStorageModeTestHarness.OpenAsync(
                         mode,
                         directory.Path,
                         clock))
        {
            await TtlStorageModeTestHarness.PutAsync(
                database,
                mode,
                "key1",
                "value1",
                TimeSpan.FromHours(1));
        }

        clock.UtcNow = DateTimeOffset.UnixEpoch.AddMinutes(30);
        await using var reopened = await TtlStorageModeTestHarness.OpenAsync(
            mode,
            directory.Path,
            clock);

        Assert.Equal("value1", await TtlStorageModeTestHarness.GetTextAsync(reopened, "key1"));
    }

    [Theory]
    [InlineData(TtlStorageMode.Local)]
    [InlineData(TtlStorageMode.Cloud)]
    public async Task ShouldPersistTtlMetadataGivenFlushAndRestartWhenReopening(TtlStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using (var database = await TtlStorageModeTestHarness.OpenAsync(
                         mode,
                         directory.Path,
                         clock))
        {
            await TtlStorageModeTestHarness.PutAsync(
                database,
                mode,
                "key1",
                "value1",
                TimeSpan.FromHours(1));
            await database.FlushAsync(database.DefaultColumnFamily);
        }

        clock.UtcNow = DateTimeOffset.UnixEpoch.AddMinutes(30);
        await using var reopened = await TtlStorageModeTestHarness.OpenAsync(
            mode,
            directory.Path,
            clock);

        Assert.Equal("value1", await TtlStorageModeTestHarness.GetTextAsync(reopened, "key1"));
    }

    [Theory]
    [InlineData(TtlStorageMode.Local)]
    [InlineData(TtlStorageMode.Cloud)]
    public async Task ShouldExpireAfterRestartGivenTtlElapsedDuringShutdownWhenReopening(
        TtlStorageMode mode)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using (var database = await TtlStorageModeTestHarness.OpenAsync(
                         mode,
                         directory.Path,
                         clock))
        {
            await TtlStorageModeTestHarness.PutAsync(
                database,
                mode,
                "key1",
                "value1",
                TimeSpan.FromSeconds(1));
            clock.UtcNow = DateTimeOffset.UnixEpoch.AddSeconds(1);
        }

        await using var reopened = await TtlStorageModeTestHarness.OpenAsync(
            mode,
            directory.Path,
            clock);

        Assert.Null(await TtlStorageModeTestHarness.GetTextAsync(reopened, "key1"));
    }

    [Theory]
    [InlineData(TtlStorageMode.Memory)]
    [InlineData(TtlStorageMode.Local)]
    [InlineData(TtlStorageMode.Cloud)]
    public async Task ShouldRemoveExpiredEntriesGivenCompactionWhenTtlExceeded(TtlStorageMode mode)
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

        await database.FlushAsync(database.DefaultColumnFamily);

        Assert.Null(await TtlStorageModeTestHarness.GetTextAsync(database, "key1"));
    }

    [Theory]
    [InlineData(TtlStorageMode.Memory)]
    [InlineData(TtlStorageMode.Local)]
    [InlineData(TtlStorageMode.Cloud)]
    public async Task ShouldPreserveNonExpiredEntriesGivenCompactionWhenTtlNotExceeded(
        TtlStorageMode mode)
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

        await database.FlushAsync(database.DefaultColumnFamily);

        Assert.Equal("value1", await TtlStorageModeTestHarness.GetTextAsync(database, "key1"));
    }

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
        await using (var writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
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

        await database.FlushAsync(database.DefaultColumnFamily);
        await using (var deleting = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            deleting.DeleteRange("k3"u8.ToArray(), "k8"u8.ToArray());
            await deleting.CommitAsync(TtlStorageModeTestHarness.GetWriteOptions(mode));
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        clock.UtcNow = DateTimeOffset.UnixEpoch.AddSeconds(1);
        await database.CompactAllAsync();

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
        await using var database = await TtlStorageModeTestHarness.OpenAsync(
            mode,
            directory.Path,
            clock);
        await using (var writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 0; index < 50; index++)
            {
                writer.Put(
                    TestBytes.FromString($"level0-key-{index:0000}"),
                    "l0-value"u8.ToArray(),
                    TimeSpan.FromSeconds(1));
            }

            await writer.CommitAsync(TtlStorageModeTestHarness.GetWriteOptions(mode));
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        await using (var writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            for (var index = 50; index < 100; index++)
            {
                writer.Put(
                    TestBytes.FromString($"level1-key-{index:0000}"),
                    "l1-value"u8.ToArray(),
                    TimeSpan.FromHours(1));
            }

            await writer.CommitAsync(TtlStorageModeTestHarness.GetWriteOptions(mode));
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        clock.UtcNow = DateTimeOffset.UnixEpoch.AddSeconds(1);
        await database.CompactAllAsync();
        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        var expiredCount = 0;
        var retainedCount = 0;
        for (var index = 0; index < 50; index++)
        {
            if (await reader.GetAsync(TestBytes.FromString($"level0-key-{index:0000}")) is null)
            {
                expiredCount++;
            }
        }

        for (var index = 50; index < 100; index++)
        {
            if (await reader.GetAsync(TestBytes.FromString($"level1-key-{index:0000}")) is not null)
            {
                retainedCount++;
            }
        }

        Assert.Equal(50, expiredCount);
        Assert.Equal(50, retainedCount);
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
        await using (var deleting = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            deleting.DeleteRange("k1"u8.ToArray(), "k9"u8.ToArray());
            await deleting.CommitAsync(TtlStorageModeTestHarness.GetWriteOptions(mode));
        }

        clock.UtcNow = DateTimeOffset.UnixEpoch.AddSeconds(1);

        Assert.Null(await TtlStorageModeTestHarness.GetTextAsync(database, "k5"));
    }
}
