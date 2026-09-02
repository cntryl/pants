using Cntryl.Pants.Destroyer.Support;
using Cntryl.Pants.Exceptions;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>sst-corruption</c> scenario: fault
/// <see cref="FaultClass.SstCorruption"/>, <see cref="FaultExpectation.SafetyPreserved"/>.
/// Flips a byte in a flushed SST's staged output (see
/// <see cref="SstByteCorruptingFailpointHandler"/>) and verifies Pants
/// detects the corruption and fails the flush with
/// <see cref="PantsCorruptionException"/> rather than publishing corrupted
/// data — and, since the commit that produced this data landed in the WAL
/// before the (later, separate) flush ever touched it, safety-preserved
/// means recovery still has it: rejecting the corrupted flush must not
/// lose the durable write that flush was building from.
/// </summary>
public sealed class SstCorruptionScenarioTests
{
    [Fact]
    public async Task ShouldFailSafelyGivenCorruptedSstBytes()
    {
        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-sst-corruption");

        var failpoints = new SstByteCorruptingFailpointHandler(directory.Path);
        await using (var database = await DestroyerFailpoints.OpenWithFailpointAsync(directory.Path, failpoints))
        {
            var family = await database.CreateColumnFamilyAsync("corrupt-target");
            await DestroyerDatabase.PutAsync(database, family, "corrupt-key", "corrupt-value", PantsWriteOptions.Sync);

            await Assert.ThrowsAsync<PantsCorruptionException>(() => database.FlushAsync(family).AsTask());
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        var recoveredFamily = await reopened.GetColumnFamilyAsync("corrupt-target");
        Assert.NotNull(recoveredFamily);
        var value = await DestroyerDatabase.GetAsync(reopened, recoveredFamily!, "corrupt-key");
        Assert.Equal("corrupt-value", value);
    }
}
