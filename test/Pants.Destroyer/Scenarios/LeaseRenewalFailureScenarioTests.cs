using Cntryl.Pants.Destroyer.Support;
using Cntryl.Pants.Exceptions;
using Cntryl.Pants.Runtime.Internal;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>lease-renewal-failure</c> scenario
/// (failpoint-tier): fault <see cref="FaultClass.LeaseRenewalCut"/>,
/// <see cref="FaultExpectation.TemporarilyUnavailable"/>. The renewal
/// failpoints in <c>Failpoint.cs</c> aren't wired to any call site yet, so
/// this instead fails renewal the way it actually fails in production: the
/// on-disk lease record is externally clobbered while the writer holds it,
/// so the writer's own heartbeat can no longer verify it still owns the
/// lease. Verifies the writer detects this and steps down (both
/// <c>IsPrimaryLeaseHealthy</c> flips false and further writes are
/// rejected) rather than continuing to write past a lease it no longer holds.
/// </summary>
public sealed class LeaseRenewalFailureScenarioTests
{
    [Fact]
    public async Task ShouldStepDownGivenLeaseRecordClobberedExternally()
    {
        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-lease-renewal-failure");

        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new RuntimeDependencies(leaseHeartbeatInterval: TimeSpan.FromMilliseconds(200)));

        await DestroyerDatabase.PutAsync(
            database, database.DefaultColumnFamily, "lease-key", "lease-value", PantsWriteOptions.Sync);
        Assert.True(database.IsPrimaryLeaseHealthy);

        // Clobber the on-disk lease record out from under the live writer -
        // its next heartbeat renewal will find a record it no longer recognizes.
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, ".midge_leader"),
            "{\"epoch\":999999,\"holderId\":\"someone-else\",\"acquiredAt\":\"" +
            DateTimeOffset.UtcNow.ToString("O") + "\"}");

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (database.IsPrimaryLeaseHealthy && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.False(database.IsPrimaryLeaseHealthy, "writer never detected its lease was clobbered");

        await Assert.ThrowsAnyAsync<PantsException>(() => DestroyerDatabase.PutAsync(
            database, database.DefaultColumnFamily, "after-step-down", "value", PantsWriteOptions.Sync).AsTask());
    }
}
