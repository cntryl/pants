using Cntryl.Pants.Destroyer.Support;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>manifest-sync-failure</c> scenario
/// (failpoint-tier): fault <see cref="FaultClass.ManifestCheckpointCut"/>,
/// <see cref="FaultExpectation.SafetyPreserved"/>. Fails the manifest
/// checkpoint replace the first time it happens after a compaction has
/// published (see <see cref="CompactionCheckpointCutFailpointHandler"/>),
/// so the checkpoint never durably lands, verifying recovery falls back to
/// the last valid manifest state rather than trusting a partially written
/// checkpoint - and, critically, the compacted data itself is not lost.
/// </summary>
public sealed class ManifestSyncFailureScenarioTests
{
    [Fact]
    public async Task ShouldFallBackToLastValidManifestGivenFailedCheckpointSync()
    {
        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-manifest-sync-failure");

        var failpoints = new CompactionCheckpointCutFailpointHandler();
        await using (var database = await DestroyerFailpoints.OpenWithFailpointAsync(directory.Path, failpoints))
        {
            var family = database.DefaultColumnFamily;
            await DestroyerDatabase.PutAsync(database, family, "checkpoint-key", "checkpoint-value", PantsWriteOptions.Sync);
            await database.FlushAsync(family);

            // The checkpoint cut fires on compaction's manifest checkpoint,
            // not the flush above; drive a compaction to trigger it. Whether
            // this specific call throws depends on timing, so don't assert on
            // it directly - the recovery check below is what matters.
            await DestroyerFailpoints.IgnoreInjectedFailureAsync(() => database.CompactAllAsync().AsTask());
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        Assert.Equal(
            "checkpoint-value",
            await DestroyerDatabase.GetAsync(reopened, reopened.DefaultColumnFamily, "checkpoint-key"));
    }
}
