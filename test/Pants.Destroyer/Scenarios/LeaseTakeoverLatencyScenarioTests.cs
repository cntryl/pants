using System.Diagnostics;
using Cntryl.Pants.Destroyer.Support;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>lease-takeover-latency</c> scenario:
/// faults <see cref="FaultClass.ProcessKill"/>, <see cref="FaultClass.LeaseStalenessWindow"/>,
/// <see cref="FaultExpectation.TemporarilyUnavailable"/>. Measures how long
/// a new writer takes to acquire the lease after the previous holder
/// crashes, and asserts it is neither instant (the split-brain guard was
/// actually enforced, not skipped) nor unbounded.
/// </summary>
public sealed class LeaseTakeoverLatencyScenarioTests
{
    [Fact]
    public async Task ShouldAcquireLeaseWithinBudgetGivenPriorHolderCrashed()
    {
        const int operationCount = 10;
        const ulong seed = 12;

        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-lease-takeover-latency");

        await DestroyerWorker.RunUntilAckedThenKillAsync(directory.Path, operationCount, seed, ackThreshold: 1);

        var stopwatch = Stopwatch.StartNew();
        await using var recovered = await DestroyerWorker.ReopenAfterLeaseTakeoverAsync(
            directory.Path, TimeSpan.FromSeconds(120));
        stopwatch.Stop();

        // The lease takeover base delay (see FileLease.Acquire) is a fixed
        // 60s floor by design, so takeover must never be near-instant...
        Assert.True(
            stopwatch.Elapsed >= TimeSpan.FromSeconds(45),
            $"lease takeover completed suspiciously fast ({stopwatch.Elapsed}) - the split-brain guard may not be enforced");
        // ...but also must not hang indefinitely once the window elapses.
        Assert.True(
            stopwatch.Elapsed <= TimeSpan.FromSeconds(100),
            $"lease takeover took too long ({stopwatch.Elapsed})");
    }
}
