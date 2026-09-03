using System.Reflection;

namespace Cntryl.Pants.Tests.Cloud;

public sealed class PantsCloudDiskResidentReadTests
{
    const int EntryCount = 96;

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
        var snapshotField = typeof(TransactionInstance).GetField(
            "_startSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var snapshot = Assert.IsType<DatabaseVersion>(snapshotField?.GetValue(transaction));

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
        await using var scan = await transaction.ScanAsync(new PantsScanQuery
        {
            Prefix = "address:00"u8.ToArray(),
            Direction = direction
        });
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
