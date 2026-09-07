using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsL0AdmissionTests
{
    const int L0FileCountTrigger = 2;

    /// <summary>
    ///     With background compaction disabled nothing drains L0, so write admission is the only
    ///     thing standing between a write-heavy caller and unbounded read amplification.
    /// </summary>
    [Fact]
    public async Task ShouldStallWritesWhenPublishedL0ReachesTheHardCeiling()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(CreateOptions(directory.Path));
        var family = database.ColumnFamilies.DefaultFamily;
        var ceiling = L0FileCountTrigger +
            MemtableWritePressure.MaximumImmutableMemtablesPerColumnFamily + 1;

        var stalled = false;
        for (var attempt = 0; attempt < ceiling * 3 && !stalled; attempt++)
        {
            try
            {
                await CommitAsync(database, family, $"key-{attempt}");
                await database.Maintenance.FlushAsync(family);
            }
            catch (PantsWriteStallException)
            {
                stalled = true;
            }
        }

        Assert.True(stalled, "Writes must eventually be refused while L0 debt cannot drain.");
        Assert.True(
            await CountL0FilesAsync(database) <= ceiling,
            "Published L0 files must never exceed the admission ceiling.");
    }

    /// <summary>
    ///     The ceiling must not wedge the engine: draining the debt has to restore writability.
    /// </summary>
    [Fact]
    public async Task ShouldResumeWritesAfterCompactionDrainsL0Debt()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(CreateOptions(directory.Path));
        var family = database.ColumnFamilies.DefaultFamily;
        var ceiling = L0FileCountTrigger +
            MemtableWritePressure.MaximumImmutableMemtablesPerColumnFamily + 1;

        for (var attempt = 0; attempt < ceiling * 3; attempt++)
        {
            try
            {
                await CommitAsync(database, family, $"key-{attempt}");
                await database.Maintenance.FlushAsync(family);
            }
            catch (PantsWriteStallException)
            {
                break;
            }
        }

        await database.Maintenance.CompactAllAsync();

        await CommitAsync(database, family, "after-drain");
    }

    static PantsOpenOptions CreateOptions(string path) =>
        PantsOpenOptions.Local(path)
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(
                L0FileCountTrigger: L0FileCountTrigger,
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
