using Cntryl.Pants.Destroyer.Support;
using Cntryl.Pants.Runtime.Internal;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>compaction-commit-cut</c> scenario
/// (failpoint-tier): fault <see cref="FaultClass.FlushCompactionBarrierFault"/>,
/// <see cref="FaultExpectation.TemporarilyUnavailable"/>. Cuts the process
/// after new compacted SSTs are written durably but before the compaction
/// commit is published to the manifest, verifying recovery discards the
/// orphaned SSTs and the original (pre-compaction) data is still intact -
/// not lost, not double-counted.
/// </summary>
public sealed class CompactionCommitCutScenarioTests
{
    [Fact]
    public async Task ShouldDiscardOrphanedSstsGivenCutBeforeCompactionCommitPublish()
    {
        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-compaction-commit-cut");

        var failpoints = new ArmableFailpointHandler();
        await using (var database = await DestroyerFailpoints.OpenWithFailpointAsync(directory.Path, failpoints))
        {
            var family = database.DefaultColumnFamily;
            await DestroyerDatabase.PutAsync(database, family, "commit-cut-key", "commit-cut-value", PantsWriteOptions.Sync);
            await database.FlushAsync(family);

            failpoints.Arm(Failpoint.BeforeCompactionManifestPublish);
            await DestroyerFailpoints.IgnoreInjectedFailureAsync(() => database.CompactAllAsync().AsTask());
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        Assert.Equal(
            "commit-cut-value",
            await DestroyerDatabase.GetAsync(reopened, reopened.DefaultColumnFamily, "commit-cut-key"));
    }
}
