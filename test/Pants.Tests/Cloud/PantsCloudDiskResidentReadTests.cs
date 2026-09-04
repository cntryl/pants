using System.Reflection;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class PantsCloudDiskResidentReadTests
{
    const int EntryCount = 96;

    [Fact]
    public async Task ShouldReleasePublishedCloudValuesFromTheCurrentRuntimeSnapshot()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await using (var writer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put(Key(0), Value(0));
            await writer.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        await using var reader = Assert.IsType<TransactionInstance>(
            await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadOnly));

        Assert.Empty(GetSnapshot(reader).Families.Single().Value);
    }

    [Fact]
    public async Task ShouldReleasePublishedCloudValuesAfterAutomaticWalThresholdFlush()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.SimulatedCloud(
                directory.Path,
                "pants-tests",
                "disk-resident-auto-flush/")
            .WithBackgroundCompaction(false)
            .WithFlushAfterWalRecordsForTesting(1);
        await using var database = await PantsDatabase.OpenAsync(options);
        await using (var writer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("automatic"u8.ToArray(), "published"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        await using var reader = Assert.IsType<TransactionInstance>(
            await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadOnly));

        Assert.Empty(GetSnapshot(reader).Families.Single().Value);
        Assert.Equal(
            "published",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync("automatic"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldReleaseOtherCloudFamilyValuesPublishedByFlushCompaction()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.SimulatedCloud(
                directory.Path,
                "pants-tests",
                "disk-resident-family-flush/")
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                long.MaxValue,
                long.MaxValue,
                TimeSpan.FromHours(1),
                int.MaxValue))
            .WithBackgroundCompaction(true);
        await using var database = await PantsDatabase.OpenAsync(options);
        var flushedFamily = await database.ColumnFamilies.CreateAsync("flushed");
        await using (var defaultWriter = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            defaultWriter.Put("unflushed"u8.ToArray(), "retained"u8.ToArray());
            await defaultWriter.CommitAsync(PantsWriteOptions.CloudAsync);
        }

        await using (var familyWriter = await database.Transactions.BeginAsync(
                         flushedFamily,
                         PantsTransactionMode.ReadWrite))
        {
            familyWriter.Put("published"u8.ToArray(), "value"u8.ToArray());
            await familyWriter.CommitAsync(PantsWriteOptions.CloudAsync);
        }

        await database.Maintenance.FlushAsync(flushedFamily);

        await using var reader = Assert.IsType<TransactionInstance>(
            await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadOnly));
        var defaultFamily = Assert.Single(
            GetSnapshot(reader).Families,
            pair => pair.Key.Id == database.ColumnFamilies.DefaultFamily.Id);
        Assert.Empty(defaultFamily.Value);
        Assert.Equal(
            "retained",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync("unflushed"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldOpenWithoutRetainingPublishedCloudValuesInTheRuntimeSnapshot()
    {
        using var directory = new TemporaryDirectory();
        await CreateCloudCorpusAsync(directory.Path);
        RemoveLocalSsts(directory.Path);

        await using var database = await OpenAsync(directory.Path);
        await using var transaction = Assert.IsType<TransactionInstance>(
            await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadOnly));
        var snapshot = GetSnapshot(transaction);

        Assert.Empty(Assert.Single(snapshot.Families).Value);
        Assert.Empty(LocalSsts(directory.Path));
    }

    [Fact]
    public async Task ShouldReadAColdCloudPointWithoutHydratingTheWholeSst()
    {
        using var directory = new TemporaryDirectory();
        await CreateCloudCorpusAsync(directory.Path);
        RemoveLocalSsts(directory.Path);

        await using var database = await OpenAsync(directory.Path);
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        var value = await transaction.GetAsync(Key(EntryCount / 2));

        Assert.Equal(Value(EntryCount / 2), value?.ToArray());
        Assert.Empty(LocalSsts(directory.Path));
    }

    [Fact]
    public async Task ShouldPreferAnUntouchedRemoteL0ValueOverANewerCompactionFile()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.SimulatedCloud(
                directory.Path,
                "pants-tests",
                "disk-resident-compaction-precedence/")
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2));
        await using var database = await PantsDatabase.OpenAsync(options);

        await CommitAndFlushAsync(database, "key", "old-value");
        await CommitAndFlushAsync(database, "zulu", "separate-file");
        await CommitAndFlushAsync(database, "key", "new-value");
        await database.Maintenance.CompactAllAsync();
        RemoveLocalSsts(directory.Path);

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = await reader.GetAsync("key"u8.ToArray());

        Assert.Equal(
            "new-value",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(value)));
        Assert.Empty(LocalSsts(directory.Path));
    }

    [Fact]
    public async Task ShouldReleaseCloudMemtableTrackingAfterReadAmplificationCompaction()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.SimulatedCloud(
                directory.Path,
                "pants-tests",
                "disk-resident-read-compaction/")
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                long.MaxValue,
                long.MaxValue,
                TimeSpan.FromHours(1),
                int.MaxValue))
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenAsync(options);
        for (var generation = 0; generation < 6; generation++)
        {
            await CommitAndFlushAsync(database, "hot-key", $"value-{generation}");
        }

        await using (var writer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("pending"u8.ToArray(), "published-by-compaction"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        await database.Maintenance.SetBackgroundCompactionAsync(true);
        await using (var trigger = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadOnly))
        {
            Assert.Equal(
                "value-5",
                TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                    await trigger.GetAsync("hot-key"u8.ToArray()))));
        }

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(1, metrics.ReadAmplificationCompactionTriggersTotal);
        Assert.Equal(0, metrics.MaximumMemtableWalSegmentGap);
        await using var reader = Assert.IsType<TransactionInstance>(
            await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadOnly));
        Assert.Empty(GetSnapshot(reader).Families.Single().Value);
        Assert.Equal(
            "published-by-compaction",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync("pending"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldHydrateOnlyEachPlannedCloudCompactionInputSetWithinItsBudget()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.SimulatedCloud(
                directory.Path,
                "pants-tests",
                "compaction-inputs/")
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2));
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            for (var batch = 0; batch < 5; batch++)
            {
                await using var writer = await database.Transactions.BeginAsync(
                    database.ColumnFamilies.DefaultFamily,
                    PantsTransactionMode.ReadWrite);
                writer.Put(
                    TestBytes.FromString($"batch:{batch}"),
                    Value(batch));
                await writer.CommitAsync(PantsWriteOptions.CloudStrict);
                await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            }
        }

        RemoveLocalSsts(directory.Path);
        await using var reopened = await PantsDatabase.OpenAsync(options);
        var before = await reopened.Diagnostics.GetStorageLayoutAsync();
        var l0Files = Assert.Single(before.Levels, static level => level.Level == 0).Files;
        Assert.Equal(5, l0Files.Count);
        var unplannedName = l0Files
            .OrderBy(static file => file.Name, StringComparer.Ordinal)
            .Last().Name;

        await reopened.Maintenance.CompactAllAsync();

        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var batch = 0; batch < 5; batch++)
        {
            Assert.Equal(Value(batch), (await reader.GetAsync(
                TestBytes.FromString($"batch:{batch}")))?.ToArray());
        }

        Assert.DoesNotContain(
            LocalSsts(directory.Path),
            path => StringComparer.Ordinal.Equals(Path.GetFileName(path), unplannedName));
        var metrics = await reopened.Diagnostics.GetRuntimeMetricsAsync();
        Assert.InRange(metrics.CompactionBufferPeakBytes, 1, metrics.CompactionBufferCapacityBytes);
        Assert.Equal(0, metrics.CompactionBufferUsedBytes);
    }

    [Theory]
    [InlineData(PantsScanDirection.Forward)]
    [InlineData(PantsScanDirection.Reverse)]
    public async Task ShouldScanColdCloudBlocksWithoutHydratingTheWholeSst(
        PantsScanDirection direction)
    {
        using var directory = new TemporaryDirectory();
        await CreateCloudCorpusAsync(directory.Path);
        RemoveLocalSsts(directory.Path);

        await using var database = await OpenAsync(directory.Path);
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var diagnosticsBefore = await database.Diagnostics.GetReadPathDiagnosticsAsync();
        await using (var scan = await transaction.ScanAsync(new PantsScanQuery
        {
            Prefix = "address:00"u8.ToArray(),
            Direction = direction
        }))
        {
            var actual = new List<string>();
            await foreach (var entry in scan)
            {
                actual.Add(TestBytes.ToText(entry.Key));
            }

            var expected = Enumerable.Range(0, EntryCount)
                .Select(static index => $"address:{index:D4}");
            if (direction == PantsScanDirection.Reverse)
            {
                expected = expected.Reverse();
            }

            Assert.Equal(expected, actual);
        }

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        var diagnosticsAfter = await database.Diagnostics.GetReadPathDiagnosticsAsync();
        Assert.True(diagnosticsAfter.DataBlocksRead > diagnosticsBefore.DataBlocksRead);
        Assert.InRange(metrics.ScanBufferPeakBytes, 1, metrics.ScanBufferCapacityBytes);
        Assert.Equal(0, metrics.ScanBufferUsedBytes);
        Assert.Empty(LocalSsts(directory.Path));
    }

    static async Task CreateCloudCorpusAsync(string path)
    {
        await using var database = await OpenAsync(path);
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        for (var index = 0; index < EntryCount; index++)
        {
            transaction.Put(Key(index), Value(index));
        }

        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        Assert.NotEmpty(CloudSsts(path));
    }

    static async Task CommitAndFlushAsync(
        IPantsDatabase database,
        string key,
        string value)
    {
        await using var writer = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        writer.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await writer.CommitAsync(PantsWriteOptions.CloudStrict);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
    }

    static ValueTask<IPantsDatabase> OpenAsync(string path) =>
        PantsDatabase.OpenAsync(
            PantsOpenOptions.SimulatedCloud(path, "pants-tests", "disk-resident/")
                .WithBackgroundCompaction(false));

    static byte[] Key(int index) => TestBytes.FromString($"address:{index:D4}");

    static byte[] Value(int index)
    {
        var value = new byte[2 * 1024];
        new Random(index).NextBytes(value);
        return value;
    }

    static DatabaseVersion GetSnapshot(TransactionInstance transaction)
    {
        var snapshotField = typeof(TransactionInstance).GetField(
            "_startSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<DatabaseVersion>(snapshotField?.GetValue(transaction));
    }

    static void RemoveLocalSsts(string path)
    {
        foreach (var file in LocalSsts(path))
        {
            File.Delete(file);
        }

        Assert.Empty(LocalSsts(path));
    }

    static string[] LocalSsts(string path) =>
        Directory.GetFiles(Path.Combine(path, "sst"), "*.sst");

    static string[] CloudSsts(string path) =>
        Directory.GetFiles(Path.Combine(path, "cloud_store", "sst"), "*.sst");
}
