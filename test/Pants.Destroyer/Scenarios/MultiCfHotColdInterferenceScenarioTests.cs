using Cntryl.Pants.Destroyer.Support;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>multi-cf-hot-cold-interference</c>
/// scenario: faults <see cref="FaultClass.ProcessKill"/>, <see cref="FaultClass.ForcedReopen"/>,
/// <see cref="FaultExpectation.TemporarilyUnavailable"/>. Simplified to an
/// in-process check (no subprocess kill): drives a write-heavy "hot" column
/// family hard enough to force flush/compaction while a "cold" column
/// family sees almost no traffic, verifying the hot CF's churn never
/// corrupts or loses the untouched cold CF's data.
/// </summary>
public sealed class MultiCfHotColdInterferenceScenarioTests
{
    [Fact]
    public async Task ShouldIsolateColdColumnFamilyGivenHotColumnFamilyChurn()
    {
        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-multi-cf-hot-cold-interference");
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));

        var hot = await database.CreateColumnFamilyAsync("hot");
        var cold = await database.CreateColumnFamilyAsync("cold");

        await DestroyerDatabase.PutAsync(database, cold, "cold-key", "cold-value", PantsWriteOptions.Sync);

        for (var i = 0; i < 200; i++)
        {
            await DestroyerDatabase.PutAsync(database, hot, $"hot-key-{i}", $"hot-value-{i}", PantsWriteOptions.Sync);
        }

        await database.FlushAsync(hot);
        await database.CompactAllAsync();

        Assert.Equal("cold-value", await DestroyerDatabase.GetAsync(database, cold, "cold-key"));
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal($"hot-value-{i}", await DestroyerDatabase.GetAsync(database, hot, $"hot-key-{i}"));
        }
    }
}
