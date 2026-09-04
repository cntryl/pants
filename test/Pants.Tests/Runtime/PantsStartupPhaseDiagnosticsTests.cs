using Cntryl.Pants.Support.TestDoubles;
using Xunit.Abstractions;

namespace Cntryl.Pants.Runtime;

public sealed class PantsStartupPhaseDiagnosticsTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRecordOrderedStartupPhasesGivenCleanPersistentOpen(bool simulatedCloud)
    {
        using var directory = new TemporaryDirectory();
        var measurements = new List<StartupPhaseMeasurement>();
        var options = simulatedCloud
            ? PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "startup-phases/")
            : PantsOpenOptions.Local(directory.Path);
        await using (var seed = await PantsDatabase.OpenAsync(options))
        {
            await seed.ShutdownAsync(TimeSpan.FromSeconds(10));
        }

        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(startupPhaseMeasurement: measurements.Add));

        var phases = measurements.Select(static measurement => measurement.Phase).ToArray();
        AssertOrdered(
            phases,
            StartupPhase.Lease,
            StartupPhase.Format,
            StartupPhase.ManifestSnapshot,
            StartupPhase.ManifestJournal,
            StartupPhase.IntentReconciliation,
            StartupPhase.SstHydration,
            StartupPhase.WalReplay,
            StartupPhase.VersionConstruction,
            StartupPhase.ServiceStartup);
        if (simulatedCloud)
        {
            Assert.Contains(StartupPhase.CloudControlHydration, phases);
        }

        Assert.All(measurements, measurement => Assert.True(measurement.AllocatedBytes >= 0));
        foreach (var measurement in measurements)
        {
            output.WriteLine(
                $"{measurement.Phase}: {measurement.Elapsed.TotalMicroseconds:F1} us, " +
                $"{measurement.AllocatedBytes} B");
        }
    }

    static void AssertOrdered(StartupPhase[] actual, params StartupPhase[] expected)
    {
        var previous = -1;
        foreach (var phase in expected)
        {
            var index = Array.IndexOf(actual, phase);
            Assert.True(index > previous, $"Expected {phase} after index {previous}: {string.Join(", ", actual)}");
            previous = index;
        }
    }
}
