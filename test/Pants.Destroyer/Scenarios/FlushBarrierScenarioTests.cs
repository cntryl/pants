using Cntryl.Pants.Destroyer.Support;
using Cntryl.Pants.Runtime.Internal;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>flush-barrier</c> scenario
/// (failpoint-tier): faults <see cref="FaultClass.FlushCompactionBarrierFault"/>,
/// <see cref="FaultClass.CompactionRace"/>, <see cref="FaultExpectation.TemporarilyUnavailable"/>.
/// Cuts the process exactly at the memtable-flush/compaction admission
/// barrier — the point that must hold before compaction may consume a
/// flush's output — verifying recovery never lets compaction observe an
/// SST that was never durably flushed.
/// </summary>
public sealed class FlushBarrierScenarioTests
{
    [Fact]
    public async Task ShouldNotLetCompactionObserveUnflushedSstGivenCutAtBarrier()
    {
        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-flush-barrier");

        var failpoints = new ArmableFailpointHandler();
        failpoints.Arm(Failpoint.BeforeFlushCompactionAdmission);
        await using (var database = await DestroyerFailpoints.OpenWithFailpointAsync(directory.Path, failpoints))
        {
            var family = database.DefaultColumnFamily;
            await DestroyerDatabase.PutAsync(database, family, "barrier-key", "barrier-value", PantsWriteOptions.Sync);

            await DestroyerFailpoints.IgnoreInjectedFailureAsync(() => database.FlushAsync(family).AsTask());
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        Assert.Equal(
            "barrier-value",
            await DestroyerDatabase.GetAsync(reopened, reopened.DefaultColumnFamily, "barrier-key"));
    }
}
