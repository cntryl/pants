using Cntryl.Pants.Destroyer.Support;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>recovery-crash-loop</c> scenario
/// (<see cref="FaultClass.ProcessKill"/>, <see cref="FaultExpectation.TemporarilyUnavailable"/>):
/// hard-kill a worker process mid-write-stream, then reopen the same
/// database path and verify every mutation the worker reported as acked
/// survived recovery. See <see cref="DestroyerWorker"/> for why this uses a
/// real subprocess kill rather than an in-process failpoint, and for why
/// the reopen must be retried until the lease takeover window elapses.
/// </summary>
public sealed class RecoveryCrashLoopScenarioTests
{
    [Fact]
    public async Task ShouldRecoverAllAckedWritesGivenProcessKilledMidStream()
    {
        const int operationCount = 40;
        const int crashAfterAckedCount = 20;
        const ulong seed = 1;

        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-recovery-crash-loop");

        var ackedKeys = await DestroyerWorker.RunUntilAckedThenKillAsync(
            directory.Path, operationCount, seed, crashAfterAckedCount);

        Assert.True(
            ackedKeys.Count >= crashAfterAckedCount,
            "worker acked fewer operations than the crash threshold before being killed");

        await using var recovered = await DestroyerWorker.ReopenAfterLeaseTakeoverAsync(
            directory.Path, TimeSpan.FromSeconds(120));

        foreach (var (sequence, key) in ackedKeys)
        {
            var expectedValue = $"destroyer-value-{seed}-{sequence}";
            var actual = await DestroyerDatabase.GetAsync(recovered, recovered.DefaultColumnFamily, key);

            Assert.True(actual is not null, $"acked key '{key}' (sequence {sequence}) is missing after recovery");
            Assert.Equal(expectedValue, actual);
        }
    }
}
