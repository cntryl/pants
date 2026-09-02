using Cntryl.Pants.Destroyer.Support;
using Cntryl.Pants.Runtime.Internal;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>wal-prune-cut</c> scenario
/// (failpoint-tier, cloud-only in midge): fault <see cref="FaultClass.CompactionRace"/>,
/// <see cref="FaultExpectation.TemporarilyUnavailable"/>. Simplified to
/// local storage (the invariant - recovery must not need a WAL segment that
/// rotation had already started removing - doesn't depend on a cloud
/// backend). Cuts the process during WAL rotation after a durable
/// checkpoint, verifying recovery still has every committed write.
/// </summary>
public sealed class WalPruneCutScenarioTests
{
    [Fact]
    public async Task ShouldNotLoseCheckpointedDataGivenCutDuringWalRotation()
    {
        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-wal-prune-cut");

        var failpoints = new ArmableFailpointHandler();
        await using (var database = await DestroyerFailpoints.OpenWithFailpointAsync(directory.Path, failpoints))
        {
            var family = database.DefaultColumnFamily;
            await DestroyerDatabase.PutAsync(database, family, "prune-key", "prune-value", PantsWriteOptions.Sync);
            await database.FlushAsync(family);

            failpoints.Arm(Failpoint.BeforeWalRotation);
            // A second write past the flush threshold nudges WAL rotation.
            await DestroyerFailpoints.IgnoreInjectedFailureAsync(() =>
                DestroyerDatabase.PutAsync(database, family, "prune-key-2", "prune-value-2", PantsWriteOptions.Sync).AsTask());
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        Assert.Equal(
            "prune-value",
            await DestroyerDatabase.GetAsync(reopened, reopened.DefaultColumnFamily, "prune-key"));
    }
}
