namespace Pants.Tests;

public sealed class PantsColumnFamilyReclamationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldReclaimDroppedColumnFamilyFilesWithoutASnapshotPin(bool simulatedCloud)
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(CreateOptions(
            directory.Path,
            simulatedCloud));
        IPantsColumnFamily family = await CreateFlushedFamilyAsync(database, "reclaim-now");
        Assert.NotEmpty(FamilyFiles(directory.Path, family.Id, simulatedCloud));

        await database.DropColumnFamilyAsync(family);

        Assert.Empty(FamilyFiles(directory.Path, family.Id, simulatedCloud));
        Assert.DoesNotContain(
            (await database.GetStorageLayoutAsync()).Levels.SelectMany(static level => level.Files),
            file => file.ColumnFamilyId == family.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldDeferReclamationUntilTheOldestSnapshotReleases(bool simulatedCloud)
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(CreateOptions(
            directory.Path,
            simulatedCloud));
        IPantsColumnFamily family = await CreateFlushedFamilyAsync(database, "reclaim-later");
        await using IPantsTransaction snapshot = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadOnly);

        await database.DropColumnFamilyAsync(family);

        Assert.Equal(
            "value",
            TestBytes.ToText((await snapshot.GetAsync(TestBytes.FromString("key")))!.Value));
        Assert.NotEmpty(FamilyFiles(directory.Path, family.Id, simulatedCloud));
        await snapshot.RollbackAsync();
        Assert.Empty(FamilyFiles(directory.Path, family.Id, simulatedCloud));
    }

    private static async ValueTask<IPantsColumnFamily> CreateFlushedFamilyAsync(
        IPantsDatabase database,
        string name)
    {
        IPantsColumnFamily family = await database.CreateColumnFamilyAsync(name);
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString("key"), TestBytes.FromString("value"));
        await transaction.CommitAsync(
            database.Options.Storage is PantsStorageConfiguration.SimulatedCloud
                ? PantsWriteOptions.CloudStrict
                : PantsWriteOptions.Buffered);
        await database.FlushAsync(family);
        return family;
    }

    private static PantsOpenOptions CreateOptions(string path, bool simulatedCloud) =>
        (simulatedCloud
            ? PantsOpenOptions.SimulatedCloud(path, "reclamation", "cf/")
            : PantsOpenOptions.Local(path))
        .WithBackgroundCompaction(false);

    private static string[] FamilyFiles(string path, uint familyId, bool simulatedCloud)
    {
        string pattern = $"{familyId:000000}_*.sst";
        string[] local = Directory.GetFiles(Path.Combine(path, "sst"), pattern);
        return simulatedCloud
            ? [.. local, .. Directory.GetFiles(Path.Combine(path, "cloud_store", "sst"), pattern)]
            : local;
    }
}
