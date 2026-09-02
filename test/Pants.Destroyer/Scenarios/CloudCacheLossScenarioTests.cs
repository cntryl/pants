using Cntryl.Pants.Destroyer.Support;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>cloud-cache-loss</c> scenario:
/// cloud-only, fault <see cref="FaultClass.CloudCacheLoss"/>,
/// <see cref="FaultExpectation.SafetyPreserved"/>. Uses Pants's in-process
/// simulated-cloud mode (standing in for midge-destroyer's Sqrzl emulator).
/// Forces the local hybrid cache below the value's size so it evicts to the
/// simulated cloud store, then verifies a read still returns the correct
/// value by falling back to the cloud object store rather than returning
/// stale or missing data.
/// </summary>
public sealed class CloudCacheLossScenarioTests
{
    [Fact]
    public async Task ShouldFallBackToCloudStoreGivenLocalCacheEviction()
    {
        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-cloud-cache-loss");
        var value = DestroyerCloud.CreateValue(256 * 1024, seed: 21);

        await using var database = await PantsDatabase.OpenAsync(
            DestroyerCloud.CreateOptions(directory.Path, "cloud-cache-loss/", localBudgetBytes: 128 * 1024));

        await DestroyerDatabase.PutBytesAsync(
            database, database.DefaultColumnFamily, "evicted-key", value, PantsWriteOptions.CloudStrict);
        await database.FlushAsync(database.DefaultColumnFamily);

        // The value is larger than the local budget, so the flush must have
        // evicted it to the simulated cloud store rather than keeping it local.
        Assert.Empty(DestroyerCloud.LocalSsts(directory.Path));
        Assert.NotEmpty(DestroyerCloud.CloudSsts(directory.Path));

        var recovered = await DestroyerDatabase.GetBytesAsync(database, database.DefaultColumnFamily, "evicted-key");
        Assert.True(recovered is not null, "read failed after local cache eviction");
        Assert.True(value.AsSpan().SequenceEqual(recovered!.Value.Span));
    }
}
