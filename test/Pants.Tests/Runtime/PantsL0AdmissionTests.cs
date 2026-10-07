using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsL0AdmissionTests
{
    const int L0FileCountTrigger = 2;

    /// <summary>
    ///     With background compaction disabled, critical L0 debt must still drain on its own so a
    ///     write-heavy caller is never wedged and published L0 stays bounded.
    /// </summary>
    [Fact]
    public async Task ShouldBoundPublishedL0WithoutCompactAllWhenBackgroundCompactionIsDisabled()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(CreateOptions(directory.Path));
        var family = database.ColumnFamilies.DefaultFamily;
        var ceiling = L0FileCountTrigger +
            MemtableWritePressure.MaximumImmutableMemtablesPerColumnFamily + 1;

        var maximumL0 = 0;
        for (var attempt = 0; attempt < ceiling * 2; attempt++)
        {
            Assert.True(
                await database.Maintenance.WaitForWriteStallClearAsync(
                    family,
                    TimeSpan.FromSeconds(30)),
                "A stalled write must clear without CompactAllAsync.");
            await CommitAsync(database, family, $"key-{attempt}");
            await database.Maintenance.FlushAsync(family);
            maximumL0 = Math.Max(maximumL0, await CountL0FilesAsync(database));
        }

        Assert.True(maximumL0 <= ceiling, $"Published L0 reached {maximumL0}; ceiling is {ceiling}.");
        await CommitAsync(database, family, "after");
    }

    [Fact]
    public async Task ShouldRecoverOverCeilingL0OnOpenWhenBackgroundCompactionIsDisabled()
    {
        using var directory = new TemporaryDirectory();
        var ceiling = L0FileCountTrigger +
            MemtableWritePressure.MaximumImmutableMemtablesPerColumnFamily + 1;
        await using (var database = await PantsDatabase.OpenAsync(
                         CreateOptions(directory.Path, L0FileCountTrigger * 100)))
        {
            var family = database.ColumnFamilies.DefaultFamily;
            for (var attempt = 0; attempt < ceiling + 2; attempt++)
            {
                await CommitAsync(database, family, $"key-{attempt}");
                await database.Maintenance.FlushAsync(family);
            }

            Assert.True(await CountL0FilesAsync(database) >= ceiling);
        }

        await using var reopened = await PantsDatabase.OpenAsync(CreateOptions(directory.Path));
        Assert.True(
            await reopened.Maintenance.WaitForWriteStallClearAsync(
                reopened.ColumnFamilies.DefaultFamily,
                TimeSpan.FromSeconds(30)));
        Assert.True(await CountL0FilesAsync(reopened) < ceiling);
        await CommitAsync(reopened, reopened.ColumnFamilies.DefaultFamily, "after-open");
    }

    static PantsOpenOptions CreateOptions(string path, int l0FileCountTrigger = L0FileCountTrigger) =>
        PantsOpenOptions.Local(path)
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(
                L0FileCountTrigger: l0FileCountTrigger,
                BackgroundEnabled: false));

    static async Task CommitAsync(IPantsDatabase database, IPantsColumnFamily family, string key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString("value"));
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static async Task<int> CountL0FilesAsync(IPantsDatabase database) =>
        (await database.Diagnostics.GetStorageLayoutAsync())
        .Levels.FirstOrDefault(level => level.Level == 0)?.FileCount ?? 0;
}
