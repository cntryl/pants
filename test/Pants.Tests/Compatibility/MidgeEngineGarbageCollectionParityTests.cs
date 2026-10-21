using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeEngineGarbageCollectionParityTests
{
    static readonly string[] AllModes = ["memory", "local", "cloud"];
    static readonly string[] DurableModes = ["local", "cloud"];

    [Fact]
    public async Task ShouldCollectOrphanedSstFilesAfterCompaction()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenAsync(options);
        var family = await database.ColumnFamilies.CreateAsync("test");

        for (var batch = 0; batch < 4; batch++)
        {
            await using var transaction = await database.Transactions.BeginAsync(
                family,
                PantsTransactionMode.ReadWrite);
            for (var index = 0; index < 25; index++)
            {
                transaction.Put(
                    TestBytes.FromString($"key_{batch}_{index:0000}"),
                    TestBytes.FromString("v1"));
            }

            await transaction.CommitAsync(PantsWriteOptions.Buffered);
            await database.Maintenance.FlushAsync(family);
        }

        var inputs = (await database.Diagnostics.GetStorageLayoutAsync())
            .Levels
            .Where(static level => level.Level == 0)
            .SelectMany(static level => level.Files)
            .Select(static file => file.Name)
            .ToArray();
        Assert.Equal(4, inputs.Length);
        var inputPaths = inputs
            .Select(name => Path.Combine(directory.Path, "sst", name))
            .ToArray();
        Assert.All(inputPaths, path => Assert.True(File.Exists(path), path));

        await database.Maintenance.CompactAllAsync();

        var compactedLayout = await database.Diagnostics.GetStorageLayoutAsync();
        Assert.NotEmpty(compactedLayout.Levels.SelectMany(static level => level.Files));
        Assert.DoesNotContain(compactedLayout.Levels, static level => level.Level == 0);
        Assert.All(inputPaths, path => Assert.False(File.Exists(path), path));

        await using var reader = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        for (var batch = 0; batch < 4; batch++)
        {
            for (var index = 0; index < 25; index++)
            {
                var key = TestBytes.FromString($"key_{batch}_{index:0000}");
                Assert.Equal(
                    "v1",
                    TestBytes.ToText((await reader.GetAsync(key))!.Value));
            }
        }
    }

    [Fact]
    public async Task ShouldCollectObsoleteInputsWhenBackgroundCompactionRuns()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithCompaction(new PantsCompactionConfiguration(
                L0FileCountTrigger: 4,
                BackgroundEnabled: true));
        await using var database = await PantsDatabase.OpenAsync(options);
        var family = await database.ColumnFamilies.CreateAsync("test");
        var inputPaths = new HashSet<string>(StringComparer.Ordinal);

        for (var batch = 0; batch < 4; batch++)
        {
            await using (var transaction = await database.Transactions.BeginAsync(
                             family,
                             PantsTransactionMode.ReadWrite))
            {
                for (var index = 0; index < 25; index++)
                {
                    transaction.Put(
                        TestBytes.FromString($"interval_key_{index:0000}"),
                        TestBytes.FromString($"v{batch}"));
                }

                await transaction.CommitAsync(PantsWriteOptions.Buffered);
            }

            await database.Maintenance.FlushAsync(family);
            var layout = await database.Diagnostics.GetStorageLayoutAsync();
            foreach (var name in layout.Levels
                         .Where(static level => level.Level == 0)
                         .SelectMany(static level => level.Files)
                         .Select(static file => file.Name))
            {
                inputPaths.Add(Path.Combine(directory.Path, "sst", name));
            }
        }

        Assert.NotEmpty(inputPaths);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (!inputPaths.Any(static path => !File.Exists(path)))
        {
            Assert.False(deadline.IsCancellationRequested, "Background compaction did not collect an L0 input.");
            await Task.Delay(200);
        }

        Assert.True((await database.Diagnostics.GetRuntimeMetricsAsync()).CompactionsRun > 0);
        await AssertRowsAsync(database, family, "interval_key_", 25, "v3");
    }

    [Fact]
    public async Task ShouldClearMemtableWalGapWhenFlushPublishesValues()
    {
        foreach (var mode in DurableModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenModeAsync(mode, directory.Path);
            var family = await database.ColumnFamilies.CreateAsync("test");
            await using (var transaction = await database.Transactions.BeginAsync(
                             family,
                             PantsTransactionMode.ReadWrite))
            {
                for (var index = 0; index < 50; index++)
                {
                    transaction.Put(
                        TestBytes.FromString($"wal_key_{index:0000}"),
                        TestBytes.FromString("wal_value"));
                }

                await transaction.CommitAsync(WriteOptions(mode));
            }

            await database.Maintenance.FlushAsync(family);
            await AssertRowsAsync(database, family, "wal_key_", 50, "wal_value");
            Assert.Equal(
                0,
                (await database.Diagnostics.GetRuntimeMetricsAsync()).MaximumMemtableWalSegmentGap);
        }
    }

    [Fact]
    public async Task ShouldNotCollectWalSegmentsStillNeededForRecovery()
    {
        foreach (var mode in DurableModes)
        {
            using var directory = new TemporaryDirectory();
            var options = OpenOptions(mode, directory.Path);
            await using (var database = await PantsDatabase.OpenAsync(options))
            {
                var family = await database.ColumnFamilies.CreateAsync("test");
                await using var transaction = await database.Transactions.BeginAsync(
                    family,
                    PantsTransactionMode.ReadWrite);
                for (var index = 0; index < 30; index++)
                {
                    transaction.Put(
                        TestBytes.FromString($"recovery_key_{index:0000}"),
                        TestBytes.FromString("recovery_value"));
                }

                await transaction.CommitAsync(WriteOptions(mode));
                await database.ShutdownAsync(TestTimeouts.Expected);
            }

            await using var reopened = await PantsDatabase.OpenAsync(options);
            var familyAfterReopen = await reopened.ColumnFamilies.GetAsync("test")
                ?? throw new InvalidOperationException("The test column family was not reopened.");
            await AssertRowsAsync(reopened, familyAfterReopen, "recovery_key_", 30, "recovery_value");
        }
    }

    [Fact]
    public async Task ShouldReadExactFlushedValuesFromManifestOwnedSsts()
    {
        foreach (var mode in AllModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenModeAsync(mode, directory.Path);
            var family = await database.ColumnFamilies.CreateAsync("test");
            await using (var transaction = await database.Transactions.BeginAsync(
                             family,
                             PantsTransactionMode.ReadWrite))
            {
                for (var index = 0; index < 50; index++)
                {
                    transaction.Put(
                        TestBytes.FromString($"key_{index:0000}"),
                        TestBytes.FromString("value"));
                }

                await transaction.CommitAsync(WriteOptions(mode));
            }

            await database.Maintenance.FlushAsync(family);
            if (mode != "memory")
            {
                var layout = await database.Diagnostics.GetStorageLayoutAsync();
                Assert.NotEmpty(layout.Levels.SelectMany(static level => level.Files));
            }

            await AssertRowsAsync(database, family, "key_", 50, "value");
        }
    }

    static ValueTask<IPantsDatabase> OpenModeAsync(string mode, string path) =>
        PantsDatabase.OpenAsync(OpenOptions(mode, path));

    static PantsOpenOptions OpenOptions(string mode, string path) => mode switch
    {
        "memory" => PantsOpenOptions.InMemory(),
        "local" => PantsOpenOptions.Local(path).WithBackgroundCompaction(false),
        "cloud" => PantsOpenOptions.SimulatedCloud(path, "pants-tests", "engine-gc/")
            .WithBackgroundCompaction(false),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown storage mode.")
    };

    static PantsWriteOptions WriteOptions(string mode) =>
        mode == "cloud" ? PantsWriteOptions.CloudAsync : PantsWriteOptions.Buffered;

    static async Task AssertRowsAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string keyPrefix,
        int count,
        string value)
    {
        await using var reader = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < count; index++)
        {
            var key = TestBytes.FromString($"{keyPrefix}{index:0000}");
            Assert.Equal(value, TestBytes.ToText((await reader.GetAsync(key))!.Value));
        }
    }
}
