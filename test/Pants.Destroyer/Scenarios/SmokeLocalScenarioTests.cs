using Cntryl.Pants.Destroyer.Support;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>smoke-local</c> scenario: local-only,
/// no fault injection, <see cref="FaultExpectation.SafetyPreserved"/>. A
/// fast baseline confirming Pants opens, writes, reads, and closes cleanly
/// against local storage with nothing else going wrong.
/// </summary>
public sealed class SmokeLocalScenarioTests
{
    [Fact]
    public async Task ShouldCompleteCleanlyGivenNoFaultInjection()
    {
        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-smoke-local");

        await using (var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)))
        {
            await DestroyerDatabase.PutAsync(
                database, database.DefaultColumnFamily, "smoke-key", "smoke-value", PantsWriteOptions.Sync);

            Assert.Equal(
                "smoke-value",
                await DestroyerDatabase.GetAsync(database, database.DefaultColumnFamily, "smoke-key"));
        }

        // A clean shutdown releases the writer lease immediately, so reopening
        // should succeed without waiting out any takeover window.
        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        Assert.Equal(
            "smoke-value",
            await DestroyerDatabase.GetAsync(reopened, reopened.DefaultColumnFamily, "smoke-key"));
    }
}
