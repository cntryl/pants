using Cntryl.Pants.Destroyer.Support;
using Cntryl.Pants.Runtime.Internal;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>wal-sync-ack-cut</c> scenario
/// (failpoint-tier): fault <see cref="FaultClass.ExactWalPathFault"/>,
/// <see cref="FaultExpectation.SafetyPreserved"/>. Cuts the process at the
/// exact instant a WAL record has been synced but before the commit
/// finishes acknowledging it, verifying the write is either fully durable
/// or fully absent after recovery — never a torn ack. Whether the cut
/// actually surfaces as a thrown exception depends on exactly where it
/// lands relative to the commit's own await points, so this doesn't assert
/// on that - only on the recovered state being one of the two valid
/// outcomes.
/// </summary>
public sealed class WalSyncAckCutScenarioTests
{
    [Fact]
    public async Task ShouldNeverTearAckGivenCutBetweenWalSyncAndAck()
    {
        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-wal-sync-ack-cut");

        var failpoints = new ArmableFailpointHandler();
        failpoints.Arm(Failpoint.AfterWalFlush);
        await using (var database = await DestroyerFailpoints.OpenWithFailpointAsync(directory.Path, failpoints))
        {
            var family = database.DefaultColumnFamily;
            await using var writer = await database.BeginTransactionAsync(family, PantsTransactionMode.ReadWrite);
            writer.Put("wal-sync-key"u8.ToArray(), "wal-sync-value"u8.ToArray());

            await DestroyerFailpoints.IgnoreInjectedFailureAsync(
                () => writer.CommitAsync(PantsWriteOptions.Sync).AsTask());
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        var value = await DestroyerDatabase.GetAsync(reopened, reopened.DefaultColumnFamily, "wal-sync-key");
        // A cut this late in the WAL path may still land durably - the
        // invariant under test is only that recovery is never partial: the
        // write is either the exact value committed, or fully absent.
        Assert.True(value is null or "wal-sync-value");
    }
}
