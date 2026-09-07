using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class SstReadViewCachingTests
{
    /// <summary>
    ///     The index is only worth building if it is shared. Rebuilding it per snapshot would move
    ///     the sort onto the commit path and cost more than the scan it replaces, so it is keyed on
    ///     the manifest and must survive reads that publish nothing.
    /// </summary>
    [Fact]
    public void ShouldReuseTheIndexUntilTheManifestChanges()
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        using var store = LocalDiskStore.Open(directory.Path, state);

        var initial = store.GetSstReadView();

        Assert.Same(initial, store.GetSstReadView());
    }

    /// <summary>
    ///     Guards against the index silently never being populated, which would leave the read path
    ///     permanently on its fallback scan with every other test still green.
    /// </summary>
    [Fact]
    public async Task ShouldIndexPublishedFilesForPointLookup()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false)))
        {
            var family = database.ColumnFamilies.DefaultFamily;
            await using (var transaction = await database.Transactions.BeginAsync(
                             family,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Put(
                    TestBytes.FromString("indexed"),
                    TestBytes.FromString("value"));
                await transaction.CommitAsync(PantsWriteOptions.Sync);
            }

            await database.Maintenance.FlushAsync(family);
        }

        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        using var store = LocalDiskStore.Open(directory.Path, state);

        var candidates = store.GetSstReadView()
            .SelectPointCandidates(0, TestBytes.FromString("indexed"), out _);

        Assert.NotEmpty(candidates);
    }
}
