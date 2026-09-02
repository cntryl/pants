using Cntryl.Pants.Destroyer.Support;
using Cntryl.Pants.Exceptions;
using Cntryl.Pants.Runtime.Internal;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>manifest-race</c> scenario: fault
/// <see cref="FaultClass.ManifestInterruption"/>, <see cref="FaultExpectation.TemporarilyUnavailable"/>.
/// Interrupts a manifest journal write right after the append but before
/// the durability sync completes, verifying the interrupted operation
/// surfaces as a failure in the moment, then recovery rolls it forward to a
/// single consistent state rather than leaving it torn.
/// </summary>
public sealed class ManifestRaceScenarioTests
{
    [Fact]
    public async Task ShouldRecoverConsistentStateGivenManifestWriteInterruptedAfterAppend()
    {
        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-manifest-race");

        var failpoints = new ArmableFailpointHandler();
        failpoints.Arm(Failpoint.AfterManifestJournalAppend);
        await using (var database = await DestroyerFailpoints.OpenWithFailpointAsync(directory.Path, failpoints))
        {
            await Assert.ThrowsAnyAsync<PantsException>(
                () => database.CreateColumnFamilyAsync("raced-cf").AsTask());
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        Assert.NotNull(await reopened.GetColumnFamilyAsync("raced-cf"));
    }
}
