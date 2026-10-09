using System.Diagnostics;
using Cntryl.Pants.Support.CrashSoak;
using Cntryl.Pants.Support.TestDoubles;
using Xunit.Abstractions;

namespace Cntryl.Pants.Soak;

/// <summary>
///     Seeded crash/soak runs: child processes run randomized ledgered workloads and are killed at
///     seeded points, and every reopen must keep each acknowledged write, hide each failed one, and
///     show each unknown transaction entirely or not at all. See docs/testing/crash-soak.md.
/// </summary>
[Collection(CrashProcessTestGroup.Name)]
public sealed class PantsCrashSoakTests(ITestOutputHelper output)
{
    const int SmokeCycles = 3;
    const int SmokeOperationsPerCycle = 40;

    readonly CrashSoakRunner _runner = new(
        typeof(PantsCrashSoakTests),
        nameof(ShouldRunLedgeredWorkloadGivenCrashSoakChildEnvironment));

    /// <summary>The child entry point; outside a crash/soak child it returns immediately.</summary>
    [Fact]
    public async Task ShouldRunLedgeredWorkloadGivenCrashSoakChildEnvironment()
    {
        if (CrashSoakChildSpec.FromEnvironment() is { } spec)
        {
            await CrashSoakChild.RunAsync(spec);
        }
    }

    [Theory]
    [InlineData(CrashSoakBackend.Local, 3_131)]
    [InlineData(CrashSoakBackend.Local, 7_717)]
    [InlineData(CrashSoakBackend.SimulatedCloud, 1_313)]
    [InlineData(CrashSoakBackend.SimulatedCloud, 4_242)]
    public async Task ShouldHonorLedgerAcrossCrashesGivenSmokeSeed(CrashSoakBackend backend, int seed)
    {
        var report = await _runner.RunAsync(
            CrashSoakScenario.FromSeed(seed, backend, SmokeOperationsPerCycle),
            SmokeCycles);

        output.WriteLine(report.ToString());
        Assert.True(report.Acked > 0, $"Seed {seed} acknowledged nothing, so it verified nothing.");
    }

    [Fact]
    [Trait("Category", "Sqrzl")]
    public async Task ShouldHonorLedgerAcrossCrashesGivenSqrzlSmokeSeed()
    {
        var report = await _runner.RunAsync(
            CrashSoakScenario.FromSeed(2_929, CrashSoakBackend.Sqrzl, SmokeOperationsPerCycle),
            SmokeCycles);

        output.WriteLine(report.ToString());
        Assert.True(report.Acked > 0, "The Sqrzl smoke seed acknowledged nothing, so it verified nothing.");
    }

    [CrashSoakFact]
    [Trait("Category", "Soak")]
    public async Task ShouldHonorLedgerGivenOptInSoak()
    {
        var backends = CrashSoakSettings.Backends;
        var seeds = new Queue<int>(CrashSoakSettings.Seeds);
        var deadline = Stopwatch.StartNew();
        var next = seeds.Count == 0 ? 0 : seeds.Max() + 1;
        var runs = 0;
        while (seeds.Count > 0 || deadline.Elapsed < CrashSoakSettings.Duration)
        {
            var seed = seeds.Count > 0 ? seeds.Dequeue() : next++;
            foreach (var backend in backends)
            {
                output.WriteLine($"seed {seed} on {backend}…");
                var report = await _runner.RunAsync(
                    CrashSoakScenario.FromSeed(seed, backend, CrashSoakSettings.OperationsPerCycle),
                    CrashSoakSettings.Cycles);
                output.WriteLine(report.ToString());
                runs++;
            }
        }

        output.WriteLine($"{runs} seed runs passed in {deadline.Elapsed}.");
    }
}
