using System.Text;
using Cntryl.Pants.Destroyer.Support;
using Cntryl.Pants.Scan;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Destroyer.Scenarios;

/// <summary>
/// Ported from midge-destroyer's <c>scan-compaction-starvation</c> scenario:
/// faults <see cref="FaultClass.ProcessKill"/>, <see cref="FaultClass.ForcedReopen"/>,
/// <see cref="FaultExpectation.TemporarilyUnavailable"/>. Crashes and
/// recovers a worker mid-write, then runs a prefix scan across every acked
/// key to verify the scan returns every acked key with its correct value —
/// no torn reads and no compaction/scan interference — after recovery.
/// (The worker may commit a few more operations than the harness observed
/// acks for before the kill lands, so the scan can legitimately return
/// more than the acked set - it must never return less, or a wrong value
/// for any key it does return.)
/// </summary>
public sealed class ScanCompactionStarvationScenarioTests
{
    [Fact]
    public async Task ShouldScanAtLeastAllAckedKeysGivenProcessKilledMidStream()
    {
        const int operationCount = 60;
        const int crashAfterAckedCount = 30;
        const ulong seed = 15;

        using var directory = DestroyerDatabase.CreateTempDirectory("pants-destroyer-scan-compaction-starvation");

        var ackedKeys = await DestroyerWorker.RunUntilAckedThenKillAsync(
            directory.Path, operationCount, seed, crashAfterAckedCount);
        Assert.NotEmpty(ackedKeys);

        var expected = ackedKeys.ToDictionary(
            entry => entry.Key,
            entry => $"destroyer-value-{seed}-{entry.Sequence}");

        await using var recovered = await DestroyerWorker.ReopenAfterLeaseTakeoverAsync(
            directory.Path, TimeSpan.FromSeconds(120));
        await using var reader = await recovered.BeginTransactionAsync(
            recovered.DefaultColumnFamily, PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(new PantsScanQuery
        {
            Prefix = Encoding.UTF8.GetBytes($"destroyer-key-{seed}-"),
        });

        var scanned = new Dictionary<string, string>();
        await foreach (var entry in scan)
        {
            scanned[Encoding.UTF8.GetString(entry.Key.Span)] = Encoding.UTF8.GetString(entry.Value.Span);
        }

        foreach (var (key, expectedValue) in expected)
        {
            Assert.True(scanned.TryGetValue(key, out var actualValue), $"acked key '{key}' missing from scan");
            Assert.Equal(expectedValue, actualValue);
        }

        // Every scanned key (acked or not) must carry a self-consistent
        // value - a corrupted/torn read would surface here even for a key
        // the harness never observed an ack for.
        foreach (var (key, actualValue) in scanned)
        {
            var sequence = key[(key.LastIndexOf('-') + 1)..];
            Assert.Equal($"destroyer-value-{seed}-{sequence}", actualValue);
        }
    }
}
