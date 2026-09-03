using System.Reflection;

namespace Cntryl.Pants.Tests.Cloud;

public sealed class PantsCloudDiskResidentReadTests
{
    const int EntryCount = 96;

    [Fact]
    public async Task ShouldReleasePublishedCloudValuesFromTheCurrentRuntimeSnapshot()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await using (var writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put(Key(0), Value(0));
            await writer.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        await using var reader = Assert.IsType<TransactionInstance>(
            await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadOnly));

        Assert.Empty(GetSnapshot(reader).Families.Single().Value);
    }

    [Fact]
    public async Task ShouldOpenWithoutRetainingPublishedCloudValuesInTheRuntimeSnapshot()
    {
        using var directory = new TemporaryDirectory();
        await CreateCloudCorpusAsync(directory.Path);
        RemoveLocalSsts(directory.Path);

        await using var database = await OpenAsync(directory.Path);
        await using var transaction = Assert.IsType<TransactionInstance>(
            await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
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
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        var value = await transaction.GetAsync(Key(EntryCount / 2));

        Assert.Equal(Value(EntryCount / 2), value?.ToArray());
        Assert.Empty(LocalSsts(directory.Path));
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
                await using var writer = await database.BeginTransactionAsync(
                    database.DefaultColumnFamily,
                    PantsTransactionMode.ReadWrite);
                writer.Put(
                    TestBytes.FromString($"batch:{batch}"),
                    Value(batch));
                await writer.CommitAsync(PantsWriteOptions.CloudStrict);
                await database.FlushAsync(database.DefaultColumnFamily);
            }
        }

        RemoveLocalSsts(directory.Path);
        await using var reopened = await PantsDatabase.OpenAsync(options);
        var before = await reopened.GetStorageLayoutAsync();
        var l0Files = Assert.Single(before.Levels, static level => level.Level == 0).Files;
        Assert.Equal(5, l0Files.Count);
        var unplannedName = l0Files
            .OrderBy(static file => file.Name, StringComparer.Ordinal)
            .Last().Name;

        await reopened.CompactAllAsync();

        await using var reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        for (var batch = 0; batch < 5; batch++)
        {
            Assert.Equal(Value(batch), (await reader.GetAsync(
                TestBytes.FromString($"batch:{batch}")))?.ToArray());
        }

        Assert.DoesNotContain(
            LocalSsts(directory.Path),
            path => StringComparer.Ordinal.Equals(Path.GetFileName(path), unplannedName));
        var metrics = await reopened.GetRuntimeMetricsAsync();
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
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        var diagnosticsBefore = await database.GetReadPathDiagnosticsAsync();
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

        var metrics = await database.GetRuntimeMetricsAsync();
        var diagnosticsAfter = await database.GetReadPathDiagnosticsAsync();
        Assert.True(diagnosticsAfter.DataBlocksRead > diagnosticsBefore.DataBlocksRead);
        Assert.InRange(metrics.ScanBufferPeakBytes, 1, metrics.ScanBufferCapacityBytes);
        Assert.Equal(0, metrics.ScanBufferUsedBytes);
        Assert.Empty(LocalSsts(directory.Path));
    }

    static async Task CreateCloudCorpusAsync(string path)
    {
        await using var database = await OpenAsync(path);
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        for (var index = 0; index < EntryCount; index++)
        {
            transaction.Put(Key(index), Value(index));
        }

        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
        await database.FlushAsync(database.DefaultColumnFamily);
        Assert.NotEmpty(CloudSsts(path));
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
