using Cntryl.Pants.Destroyer.Support;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>delete-space-amplification</c> scenario:
/// faults <see cref="FaultClass.ProcessKill"/>, <see cref="FaultClass.ForcedReopen"/>,
/// <see cref="FaultExpectation.SafetyPreserved"/>. Crashes a worker
/// mid-write, recovers, then deletes every recovered key and compacts,
/// verifying deleted keys stay deleted (not resurrected by compaction)
/// after both a crash/recovery cycle and a tombstone-heavy compaction.
/// </summary>
public sealed class DeleteSpaceAmplificationScenarioTests
{
    [Fact]
    public async Task ShouldKeepDeletesTombstonedAfterRecoveryAndCompaction()
    {
        const int operationCount = 30;
        const int crashAfterAckedCount = 15;
        const ulong seed = 13;

        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-delete-space-amplification");

        var ackedKeys = await DestroyerWorker.RunUntilAckedThenKillAsync(
            directory.Path, operationCount, seed, crashAfterAckedCount);
        Assert.NotEmpty(ackedKeys);

        await using var recovered = await DestroyerWorker.ReopenAfterLeaseTakeoverAsync(
            directory.Path, TimeSpan.FromSeconds(120));

        foreach (var (_, key) in ackedKeys)
        {
            await DestroyerDatabase.DeleteAsync(
                recovered, recovered.DefaultColumnFamily, key, PantsWriteOptions.Sync);
        }

        await recovered.CompactAllAsync();

        foreach (var (_, key) in ackedKeys)
        {
            var value = await DestroyerDatabase.GetAsync(recovered, recovered.DefaultColumnFamily, key);
            Assert.True(value is null, $"deleted key '{key}' resurfaced after compaction");
        }
    }
}
