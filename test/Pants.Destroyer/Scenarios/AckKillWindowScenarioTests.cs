using Cntryl.Pants.Destroyer.Support;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>ack-kill-window</c> scenario: fault
/// <see cref="FaultClass.AckBeforeReportCrash"/>, <see cref="FaultExpectation.SafetyPreserved"/>.
/// Kills the worker the instant it has reported its very first ack — the
/// narrowest possible crash window between a commit being durably
/// acknowledged and anything after it — verifying that single commit still
/// survives recovery.
/// </summary>
public sealed class AckKillWindowScenarioTests
{
    [Fact]
    public async Task ShouldPreserveDurableAckGivenProcessKilledImmediatelyAfterFirstAck()
    {
        const int operationCount = 10;
        const int crashAfterAckedCount = 1;
        const ulong seed = 11;

        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-ack-kill-window");

        var ackedKeys = await DestroyerWorker.RunUntilAckedThenKillAsync(
            directory.Path, operationCount, seed, crashAfterAckedCount);

        Assert.NotEmpty(ackedKeys);
        var (sequence, key) = ackedKeys[0];

        await using var recovered = await DestroyerWorker.ReopenAfterLeaseTakeoverAsync(
            directory.Path, TimeSpan.FromSeconds(120));

        var actual = await DestroyerDatabase.GetAsync(recovered, recovered.DefaultColumnFamily, key);
        Assert.Equal($"destroyer-value-{seed}-{sequence}", actual);
    }
}
