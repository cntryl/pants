using Cntryl.Pants.Destroyer.Support;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>uuid-compaction-pressure</c> scenario:
/// faults <see cref="FaultClass.ProcessKill"/>, <see cref="FaultClass.ForcedReopen"/>,
/// <see cref="FaultExpectation.TemporarilyUnavailable"/>. Simplified from
/// midge-destroyer's high-entropy UUID key generator to a larger uniform
/// write volume (still real: multiple SST flushes), crashed and recovered
/// mid-stream, then forced through an explicit compaction to verify no
/// acked data is lost while under compaction pressure.
/// </summary>
public sealed class UuidCompactionPressureScenarioTests
{
    [Fact]
    public async Task ShouldRetainAllAckedDataGivenCompactionAfterCrashRecovery()
    {
        const int operationCount = 250;
        const int crashAfterAckedCount = 120;
        const ulong seed = 14;

        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-uuid-compaction-pressure");

        var ackedKeys = await DestroyerWorker.RunUntilAckedThenKillAsync(
            directory.Path, operationCount, seed, crashAfterAckedCount);
        Assert.True(ackedKeys.Count >= crashAfterAckedCount);

        await using var recovered = await DestroyerWorker.ReopenAfterLeaseTakeoverAsync(
            directory.Path, TimeSpan.FromSeconds(120));

        await recovered.CompactAllAsync();

        foreach (var (sequence, key) in ackedKeys)
        {
            var expected = $"destroyer-value-{seed}-{sequence}";
            var actual = await DestroyerDatabase.GetAsync(recovered, recovered.DefaultColumnFamily, key);
            Assert.Equal(expected, actual);
        }
    }
}
