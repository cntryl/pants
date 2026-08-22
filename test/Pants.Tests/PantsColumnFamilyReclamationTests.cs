namespace Pants.Tests;

public sealed class PantsColumnFamilyReclamationTests
{
    [Fact]
    public async Task ShouldReclaimDroppedColumnFamilyFilesWithoutASnapshotPin()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        IPantsColumnFamily family = await CreateFlushedFamilyAsync(database, "reclaim-now");
        Assert.NotEmpty(FamilyFiles(directory.Path, family.Id));

        await database.DropColumnFamilyAsync(family);

        Assert.Empty(FamilyFiles(directory.Path, family.Id));
        Assert.DoesNotContain(
            (await database.GetStorageLayoutAsync()).Levels.SelectMany(static level => level.Files),
            file => file.ColumnFamilyId == family.Id);
    }

    [Fact]
    public async Task ShouldDeferReclamationUntilTheOldestSnapshotReleases()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        IPantsColumnFamily family = await CreateFlushedFamilyAsync(database, "reclaim-later");
        await using IPantsTransaction snapshot = await database.BeginTransactionAsync(
            family,
            PantsTransactionMode.ReadOnly);

        await database.DropColumnFamilyAsync(family);

        Assert.Equal(
            "value",
            TestBytes.ToText((await snapshot.GetAsync(TestBytes.FromString("key")))!.Value));
        Assert.NotEmpty(FamilyFiles(directory.Path, family.Id));
        await snapshot.RollbackAsync();
        Assert.Empty(FamilyFiles(directory.Path, family.Id));
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
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
        await database.FlushAsync(family);
        return family;
    }

    private static string[] FamilyFiles(string path, uint familyId) =>
        Directory.GetFiles(Path.Combine(path, "sst"), $"{familyId:000000}_*.sst");
}
