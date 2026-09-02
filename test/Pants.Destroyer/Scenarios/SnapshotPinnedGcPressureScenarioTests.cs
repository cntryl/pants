using Cntryl.Pants.Destroyer.Support;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>snapshot-pinned-gc-pressure</c>
/// scenario: faults <see cref="FaultClass.ProcessKill"/>, <see cref="FaultClass.ForcedReopen"/>,
/// <see cref="FaultExpectation.SafetyPreserved"/>. Simplified to an
/// in-process check (no subprocess kill): the invariant under test —
/// garbage collection must never reclaim data a still-open snapshot can
/// see — doesn't depend on process death, only on holding a read-only
/// transaction open across deletes and compaction.
/// </summary>
public sealed class SnapshotPinnedGcPressureScenarioTests
{
    [Fact]
    public async Task ShouldPreserveSnapshotVisibleDataGivenDeleteAndCompactionWhilePinned()
    {
        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-snapshot-pinned-gc-pressure");
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));

        await DestroyerDatabase.PutAsync(
            database, database.DefaultColumnFamily, "pinned-key", "pinned-value", PantsWriteOptions.Sync);

        // Pin a snapshot by holding a read-only transaction open.
        await using var pinnedSnapshot = await database.BeginTransactionAsync(
            database.DefaultColumnFamily, PantsTransactionMode.ReadOnly);

        await DestroyerDatabase.DeleteAsync(
            database, database.DefaultColumnFamily, "pinned-key", PantsWriteOptions.Sync);
        await database.CompactAllAsync();

        var pinnedValue = await pinnedSnapshot.GetAsync("pinned-key"u8.ToArray());
        Assert.True(pinnedValue is not null, "pinned snapshot lost visibility of data GC should not have reclaimed");
        Assert.Equal("pinned-value", System.Text.Encoding.UTF8.GetString(pinnedValue!.Value.Span));

        var freshValue = await DestroyerDatabase.GetAsync(database, database.DefaultColumnFamily, "pinned-key");
        Assert.Null(freshValue);
    }
}
