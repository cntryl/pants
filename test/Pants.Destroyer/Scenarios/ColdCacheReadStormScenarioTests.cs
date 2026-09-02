using Cntryl.Pants.Destroyer.Support;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>cold-cache-read-storm</c> scenario:
/// cloud-only, faults <see cref="FaultClass.CloudCacheLoss"/>,
/// <see cref="FaultClass.ProviderLatencySpike"/>, <see cref="FaultExpectation.TemporarilyUnavailable"/>.
/// Evicts several values out of the local cache (see
/// <see cref="CloudCacheLossScenarioTests"/> for the single-read version of
/// this check), then drives a read "storm" - many repeated cold reads
/// across all of them - verifying every read stays correct under sustained
/// cold-cache load rather than degrading after the first fetch.
/// </summary>
public sealed class ColdCacheReadStormScenarioTests
{
    [Fact]
    public async Task ShouldServeCorrectReadsGivenSustainedColdCacheLoad()
    {
        const int keyCount = 5;
        const int readsPerKey = 20;

        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-cold-cache-read-storm");
        await using var database = await PantsDatabase.OpenAsync(
            DestroyerCloud.CreateOptions(directory.Path, "cold-cache-read-storm/", localBudgetBytes: 128 * 1024));

        var values = new Dictionary<string, byte[]>();
        for (var i = 0; i < keyCount; i++)
        {
            var key = $"storm-key-{i}";
            var value = DestroyerCloud.CreateValue(256 * 1024, seed: 30 + i);
            values[key] = value;
            await DestroyerDatabase.PutBytesAsync(database, database.DefaultColumnFamily, key, value, PantsWriteOptions.CloudStrict);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        Assert.Empty(DestroyerCloud.LocalSsts(directory.Path));
        Assert.NotEmpty(DestroyerCloud.CloudSsts(directory.Path));

        foreach (var (key, expected) in values)
        {
            for (var read = 0; read < readsPerKey; read++)
            {
                var actual = await DestroyerDatabase.GetBytesAsync(database, database.DefaultColumnFamily, key);
                Assert.True(actual is not null, $"read {read} for '{key}' failed under cold-cache load");
                Assert.True(expected.AsSpan().SequenceEqual(actual!.Value.Span), $"read {read} for '{key}' returned wrong bytes");
            }
        }
    }
}
