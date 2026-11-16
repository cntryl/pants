using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsCompactionFamilyIsolationTests
{
    [Fact]
    public async Task ShouldDrainHealthyFamilyGivenAnotherFamilysCompactionFails()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var failpoint = new NthCompactionPublicationFailpointHandler(1);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            Options(directory.Path),
            new RuntimeDependencies(failpoint));
        var other = await database.ColumnFamilies.CreateAsync("other");
        await SeedL0FilesAsync(database, database.ColumnFamilies.DefaultFamily);
        await SeedL0FilesAsync(database, other);

        // Act
        await Assert.ThrowsAsync<PantsIOException>(() => database.Maintenance.CompactAllAsync().AsTask());

        // Assert
        Assert.Equal(1, failpoint.FailureCount);
        var files = (await database.Diagnostics.GetStorageLayoutAsync()).Levels
            .SelectMany(static level => level.Files)
            .ToArray();
        var familiesWithL0 = files
            .Where(static file => file.Level == 0)
            .Select(static file => file.ColumnFamilyId)
            .Distinct()
            .ToArray();
        Assert.Single(familiesWithL0);
    }

    [Fact]
    public async Task ShouldDrainFailedFamilyOnNextCompactionGivenTheFailureWasTransient()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var failpoint = new NthCompactionPublicationFailpointHandler(1);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            Options(directory.Path),
            new RuntimeDependencies(failpoint));
        var other = await database.ColumnFamilies.CreateAsync("other");
        await SeedL0FilesAsync(database, database.ColumnFamilies.DefaultFamily);
        await SeedL0FilesAsync(database, other);
        await Assert.ThrowsAsync<PantsIOException>(() => database.Maintenance.CompactAllAsync().AsTask());

        // Act
        await database.Maintenance.CompactAllAsync();

        // Assert
        var layout = await database.Diagnostics.GetStorageLayoutAsync();
        Assert.DoesNotContain(layout.Levels, static level => level.Level == 0);
    }

    static PantsOpenOptions Options(string path) =>
        PantsOpenOptions.Local(path)
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(
                L0FileCountTrigger: 2,
                BackgroundEnabled: false));

    static async Task SeedL0FilesAsync(IPantsDatabase database, IPantsColumnFamily family)
    {
        for (var index = 0; index < 2; index++)
        {
            await using var transaction = await database.Transactions.BeginAsync(
                family,
                PantsTransactionMode.ReadWrite);
            transaction.Put(
                TestBytes.FromString($"key-{index}"),
                TestBytes.FromString($"value-{index}"));
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
            await database.Maintenance.FlushAsync(family);
        }
    }
}
