using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

/// <summary>
///     Hybrid compaction streams cold inputs through ranged reads and drains every output partition
///     to cloud storage before staging the next one, so its local footprint is one fixed staging
///     window however large the inputs are.
/// </summary>
public sealed class PantsCloudCompactionStagingTests
{
    const long LocalBudgetBytes = 128 * 1024;
    const int FlushCount = 4;
    const int EntriesPerFlush = 32;
    const int ValueBytes = 2 * 1024;

    [Fact]
    public async Task ShouldKeepPeakLocalBytesWithinTheBudgetWhileCompactingInputsLargerThanIt()
    {
        using var directory = new TemporaryDirectory();
        await SeedAsync(directory.Path);
        RemoveLocalSsts(directory.Path);
        var inputBytes = CloudSsts(directory.Path).Sum(static path => new FileInfo(path).Length);
        Assert.True(inputBytes > LocalBudgetBytes, $"Inputs of {inputBytes} bytes must exceed the budget.");
        var sampler = new StagingFailpointHandler(directory.Path);

        await using (var database = await OpenAsync(directory.Path, sampler))
        {
            await database.Maintenance.CompactAllAsync();
            await AssertReadableAsync(database);
        }

        Assert.True(sampler.Samples.Count > 1, "Expected several staged output partitions.");
        Assert.All(sampler.Samples, sample => Assert.InRange(sample.LocalBytes, 0, LocalBudgetBytes));
        Assert.True(sampler.Samples.Max(static sample => sample.LocalBytes) > 0);
    }

    [Fact]
    public async Task ShouldDrainEachOutputPartitionBeforeStagingTheNext()
    {
        using var directory = new TemporaryDirectory();
        await SeedAsync(directory.Path);
        RemoveLocalSsts(directory.Path);
        var sampler = new StagingFailpointHandler(directory.Path);

        await using var database = await OpenAsync(directory.Path, sampler);
        await database.Maintenance.CompactAllAsync();

        var staged = sampler.Samples
            .Where(static sample => sample.Failpoint == Failpoint.AfterCompactionOutputDurable)
            .ToArray();
        var drained = sampler.Samples
            .Where(static sample => sample.Failpoint == Failpoint.AfterCompactionPartitionDrained)
            .ToArray();
        Assert.True(staged.Length > 1, "Expected several output partitions.");
        Assert.Equal(staged.Length, drained.Length);
        Assert.All(staged, sample => Assert.Empty(sample.LocalSsts));
        Assert.All(drained, sample => Assert.Empty(sample.LocalSsts));
        var outputs = (await database.Diagnostics.GetStorageLayoutAsync())
            .Levels.SelectMany(static level => level.Files)
            .Select(static file => file.Name)
            .ToArray();
        Assert.Equal(staged.Length, outputs.Length);
        Assert.All(outputs, name => Assert.True(File.Exists(Path.Combine(
            directory.Path,
            "cloud_store",
            "sst",
            name))));
        await AssertReadableAsync(database);
    }

    [Fact]
    public async Task ShouldFailTypedWithoutDeletingInputsWhenAnOutputCannotFitTheStagingWindow()
    {
        using var directory = new TemporaryDirectory();
        var big = new byte[LocalBudgetBytes * 3 / 4];
        new Random(7).NextBytes(big);
        await using (var seed = await PantsDatabase.OpenAsync(SeedOptions(directory.Path)))
        {
            await PutAndFlushAsync(seed, "big", big);
            await PutAndFlushAsync(seed, "small", [1, 2, 3]);
        }

        RemoveLocalSsts(directory.Path);
        var inputs = CloudSsts(directory.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();

        await using var database = await OpenAsync(directory.Path, NullPantsFailpointHandler.Instance);
        var before = await LayoutNamesAsync(database);

        await Assert.ThrowsAsync<PantsNoSpaceException>(() =>
            database.Maintenance.CompactAllAsync().AsTask());

        Assert.Equal(before, await LayoutNamesAsync(database));
        Assert.Equal(inputs, CloudSsts(directory.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(
            Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*", SearchOption.AllDirectories),
            static path => path.EndsWith(".sst", StringComparison.Ordinal) ||
                           path.EndsWith(".tmp", StringComparison.Ordinal));
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(big, (await reader.GetAsync(TestBytes.FromString("big")))?.ToArray());
        Assert.Equal([1, 2, 3], (await reader.GetAsync(TestBytes.FromString("small")))?.ToArray());
    }

    [Fact]
    public async Task ShouldRecoverAndRetryAfterFailingWithSomePartitionsDrained()
    {
        using var directory = new TemporaryDirectory();
        await SeedAsync(directory.Path);
        RemoveLocalSsts(directory.Path);
        string[] inputs;
        var failing = new StagingFailpointHandler(
            directory.Path,
            Failpoint.AfterCompactionPartitionDrained,
            2);
        await using (var database = await OpenAsync(directory.Path, failing))
        {
            inputs = await LayoutNamesAsync(database);
            await Assert.ThrowsAnyAsync<PantsException>(() =>
                database.Maintenance.CompactAllAsync().AsTask());
            Assert.Equal(inputs, await LayoutNamesAsync(database));
        }

        Assert.True(failing.Samples.Count(static sample =>
            sample.Failpoint == Failpoint.AfterCompactionPartitionDrained) >= 2);

        await using (var reopened = await OpenAsync(directory.Path, NullPantsFailpointHandler.Instance))
        {
            Assert.Equal(inputs, await LayoutNamesAsync(reopened));
            // The drained partitions were named by the compaction intent before they were
            // uploaded, so recovery rolls them back along with the intent.
            Assert.Equal(
                inputs,
                CloudSsts(directory.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            await AssertReadableAsync(reopened);
            await reopened.Maintenance.CompactAllAsync();
            Assert.DoesNotContain(
                await LayoutNamesAsync(reopened),
                name => inputs.Contains(name, StringComparer.Ordinal));
            await AssertReadableAsync(reopened);
            Assert.Equal(
                PantsEngineHealth.Healthy,
                (await reopened.Diagnostics.GetRuntimeMetricsAsync()).Health);
        }

        await using var final = await OpenAsync(directory.Path, NullPantsFailpointHandler.Instance);
        await AssertReadableAsync(final);
    }

    [Fact]
    public async Task ShouldRecoverDrainedOutputsPublishedJustBeforeACrash()
    {
        using var directory = new TemporaryDirectory();
        await SeedAsync(directory.Path);
        RemoveLocalSsts(directory.Path);
        var failing = new StagingFailpointHandler(
            directory.Path,
            Failpoint.AfterCompactionManifestPublish,
            1);
        await using (var database = await OpenAsync(directory.Path, failing))
        {
            await Assert.ThrowsAnyAsync<PantsException>(() =>
                database.Maintenance.CompactAllAsync().AsTask());
        }

        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));

        await using (var reopened = await OpenAsync(directory.Path, NullPantsFailpointHandler.Instance))
        {
            await AssertReadableAsync(reopened);
            await reopened.Maintenance.CompactAllAsync();
            await AssertReadableAsync(reopened);
        }

        await using var final = await OpenAsync(directory.Path, NullPantsFailpointHandler.Instance);
        await AssertReadableAsync(final);
    }

    static PantsOpenOptions SeedOptions(string path) =>
        PantsOpenOptions.SimulatedCloud(path, "pants-tests", "compaction-staging/")
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(
                L0FileCountTrigger: 2,
                BackgroundEnabled: false));

    static Task<IPantsDatabase> OpenAsync(string path, IFailpointHandler failpoints) =>
        PantsDatabase.OpenForTestingAsync(
            SeedOptions(path).WithSimulatedCloudLocalStorageBudget(LocalBudgetBytes),
            new RuntimeDependencies(failpoints)).AsTask();

    static async Task SeedAsync(string path)
    {
        await using var database = await PantsDatabase.OpenAsync(SeedOptions(path));
        for (var flush = 0; flush < FlushCount; flush++)
        {
            await using (var writer = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                for (var entry = 0; entry < EntriesPerFlush; entry++)
                {
                    var index = (flush * EntriesPerFlush) + entry;
                    writer.Put(Key(index), Value(index));
                }

                await writer.CommitAsync(PantsWriteOptions.CloudStrict);
            }

            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }
    }

    static async Task PutAndFlushAsync(IPantsDatabase database, string key, byte[] value)
    {
        await using (var writer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put(TestBytes.FromString(key), value);
            await writer.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }

    static async Task AssertReadableAsync(IPantsDatabase database)
    {
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < FlushCount * EntriesPerFlush; index++)
        {
            Assert.Equal(Value(index), (await reader.GetAsync(Key(index)))?.ToArray());
        }
    }

    static async Task<string[]> LayoutNamesAsync(IPantsDatabase database) =>
        (await database.Diagnostics.GetStorageLayoutAsync())
        .Levels.SelectMany(static level => level.Files)
        .Select(static file => file.Name)
        .Order(StringComparer.Ordinal)
        .ToArray();

    static byte[] Key(int index) => TestBytes.FromString($"staging:{index:0000}");

    static byte[] Value(int index)
    {
        var value = new byte[ValueBytes];
        new Random(index).NextBytes(value);
        return value;
    }

    static void RemoveLocalSsts(string path)
    {
        foreach (var file in Directory.GetFiles(Path.Combine(path, "sst"), "*.sst"))
        {
            File.Delete(file);
        }
    }

    static string[] CloudSsts(string path) =>
        Directory.GetFiles(Path.Combine(path, "cloud_store", "sst"), "*.sst");

    sealed record StagingSample(Failpoint Failpoint, long LocalBytes, string[] LocalSsts);

    /// <summary>
    ///     Samples the local SST and WAL bytes on disk, staging files included, each time an output
    ///     partition is staged or drained, and optionally fails the Nth hit of one failpoint.
    /// </summary>
    sealed class StagingFailpointHandler(
        string databasePath,
        Failpoint? failAt = null,
        int failOnHit = 0) : IFailpointHandler
    {
        readonly Lock _gate = new();
        readonly List<StagingSample> _samples = [];
        int _hits;

        public IReadOnlyList<StagingSample> Samples
        {
            get
            {
                lock (_gate)
                {
                    return [.. _samples];
                }
            }
        }

        public void Hit(Failpoint failpoint)
        {
            if (failpoint is Failpoint.AfterCompactionOutputDurable or Failpoint.AfterCompactionPartitionDrained)
            {
                var sample = new StagingSample(
                    failpoint,
                    LocalBytes("sst") + LocalBytes("wal"),
                    Directory.GetFiles(Path.Combine(databasePath, "sst"), "*.sst")
                        .Select(static path => Path.GetFileName(path))
                        .ToArray());
                lock (_gate)
                {
                    _samples.Add(sample);
                }
            }

            if (failpoint == failAt && Interlocked.Increment(ref _hits) == failOnHit)
            {
                throw new IOException($"Injected failure at {failpoint}.");
            }
        }

        long LocalBytes(string directory)
        {
            var path = Path.Combine(databasePath, directory);
            return Directory.Exists(path)
                ? Directory.GetFiles(path, "*", SearchOption.AllDirectories)
                    .Sum(static file => new FileInfo(file).Length)
                : 0;
        }
    }
}
