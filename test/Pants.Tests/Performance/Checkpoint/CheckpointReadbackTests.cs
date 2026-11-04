using System.Collections.Immutable;
using System.Diagnostics;
using Cntryl.Pants.Checkpoint;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Performance.Checkpoint;

public sealed class CheckpointReadbackTests
{
    [Fact]
    public async Task ShouldRetainEveryMissingPreregisteredAttemptAsInvalidRatherThanDropItsRow()
    {
        using var directory = new TemporaryDirectory();

        var report = await CheckpointCampaignReadback.EvaluateAsync(directory.Path);

        Assert.False(report.Complete);
        Assert.False(report.PolicyPermitted);
        Assert.Equal(9, report.Attempts.Length);
        Assert.All(report.Attempts, static row => { Assert.False(row.Valid); Assert.Null(row.Miss); });
    }

    [Fact]
    public async Task ShouldTerminateActualHeldWriterWhenSamplingContinuesWithoutGenuineProgress()
    {
        using var directory = new TemporaryDirectory();
        var ready = Path.Combine(directory.Path, "ready");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
        foreach (var argument in new[] { typeof(CheckpointCells).Assembly.Location, "scaleladder-crash-child",
                     Path.Combine(directory.Path, "database"), "1", ready })
        {
            start.ArgumentList.Add(argument);
        }
        var time = new ManualTimeProvider();
        var samples = 0;
        var observedReady = false;

        var result = await CheckpointProcess.RunAsync(start, "held-native-writer", TimeSpan.FromSeconds(15), () =>
        {
            samples++;
            if (!File.Exists(ready))
            {
                return 0UL;
            }
            time.Advance(observedReady ? TimeSpan.FromSeconds(61) : TimeSpan.FromSeconds(1));
            observedReady = true;
            return 1UL;
        }, TimeSpan.FromSeconds(60), time);

        Assert.True(observedReady);
        Assert.True(samples >= 2);
        Assert.True(result.Killed);
        Assert.False(result.Success);
        Assert.Equal("genuine progress allowance expired", result.TerminationReason);
    }

    [Fact]
    public async Task ShouldRetainActualFailedChildOutcomeWithoutFabricatingSuccessfulMeasurement()
    {
        using var directory = new TemporaryDirectory();
        var selection = CheckpointCells.Attempts[0];
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
        start.ArgumentList.Add("--checkpoint-intentionally-invalid-native-command");
        var outcome = await CheckpointProcess.RunAsync(start, selection.Cell.Workload, TimeSpan.FromSeconds(10));
        Assert.True(outcome.ProcessId > 0);
        Assert.False(outcome.Success);
        var attemptDirectory = Path.Combine(directory.Path, "attempts", selection.Key);
        Directory.CreateDirectory(attemptDirectory);
        var captured = CheckpointArchive.Capture(attemptDirectory, Path.Combine(directory.Path, selection.Key + ".zip"),
            selection.Key, outcome);
        var provenance = new CheckpointEvidence.Provenance("diagnostic", directory.Path, "", "", false, "source.zip", "", "", [],
            ".NET diagnostic process", "diagnostic", "diagnostic", "actual failed child", ["diagnostic, not qualified source evidence"]);
        await CheckpointEvidence.WriteAsync(Path.Combine(directory.Path, "campaign.json"),
            new CheckpointEvidence.Campaign(provenance, ImmutableArray.Create(captured)));

        var report = await CheckpointCampaignReadback.EvaluateAsync(directory.Path);

        var row = Assert.Single(report.Attempts, row => row.Key == selection.Key);
        Assert.False(report.Complete);
        Assert.False(report.PolicyPermitted);
        Assert.False(row.Valid);
        Assert.Null(row.Miss);
        Assert.NotNull(row.NativeOutcome);
        Assert.Equal(outcome.ExitCode, row.NativeOutcome.ExitCode);
        Assert.Equal(outcome.StandardError, row.NativeOutcome.StandardError);
        Assert.Contains(row.InvalidReasons, static reason => reason.Contains("Actual child process failed", StringComparison.Ordinal));
    }
}
