using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Cntryl.Pants.Storage.Internal.IO;

namespace Cntryl.Pants.Checkpoint;

static class CheckpointCampaignCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args is ["checkpoint-build-identity", var identityPath])
            {
                await CheckpointEvidence.WriteAsync(identityPath, CheckpointProvenance.ObserveLoadedBuild()).ConfigureAwait(false);
                return 0;
            }
            if (args is ["checkpoint-readback-internal", var internalRoot, var readerOutput])
            {
                var report = await CheckpointCampaignReadback.EvaluateAsync(internalRoot).ConfigureAwait(false);
                var loaded = CheckpointProvenance.ObserveLoadedBuild();
                var campaign = await CheckpointEvidence.ReadAsync<CheckpointEvidence.Campaign>(Path.Combine(internalRoot, "campaign.json")).ConfigureAwait(false);
                if (!CheckpointCampaignReadback.MatchesLoadedBuild(campaign.Provenance, loaded))
                {
                    report = report with
                    {
                        Complete = false,
                        PolicyPermitted = false,
                        InvalidReasons = report.InvalidReasons.Add("Readback executable differs from the fresh archived-source build.")
                    };
                }
                report = report with
                {
                    ReaderProcessId = Environment.ProcessId,
                    ReaderLoadedBuild = loaded
                };
                await CheckpointEvidence.WriteAsync(readerOutput, report).ConfigureAwait(false);
                return report.Complete ? 0 : 1;
            }
            if (args is ["checkpoint-readback", var root])
            {
                return await ReadFreshAsync(root).ConfigureAwait(false);
            }
            if (args is ["checkpoint-cell", var workload, var repeatText, var campaignId, var output, var database])
            {
                var selection = CheckpointCells.Select(workload, int.Parse(repeatText, CultureInfo.InvariantCulture));
                var attempt = await CheckpointCellRunner.RunAsync(campaignId, selection, output, database).ConfigureAwait(false);
                return attempt.Passed ? 0 : 1;
            }
            if (args is ["checkpoint-campaign", var artifacts, var databases, var source])
            {
                return await CampaignAsync(artifacts, databases, source).ConfigureAwait(false);
            }
            Console.Error.WriteLine("Usage: checkpoint-campaign <fresh-artifacts> <fresh-databases> <source-root> | checkpoint-readback <artifacts>");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    static async Task<int> CampaignAsync(string artifacts, string databases, string source)
    {
        artifacts = StoragePathGuard.CanonicalizeRoot(artifacts);
        databases = StoragePathGuard.CanonicalizeRoot(databases);
        source = StoragePathGuard.CanonicalizeRoot(source);
        if (Directory.Exists(artifacts) || Directory.Exists(databases) || Overlap(artifacts, databases) ||
            Overlap(artifacts, source) || Overlap(databases, source))
        {
            throw new InvalidOperationException("Campaign artifact/database roots must be new, mutually disjoint and outside the source; failed databases are retained.");
        }
        Directory.CreateDirectory(artifacts);
        Directory.CreateDirectory(databases);
        var provenance = await CheckpointProvenance.CaptureAsync(source, artifacts).ConfigureAwait(false);
        var captured = ImmutableArray.CreateBuilder<CheckpointEvidence.CapturedAttempt>(9);
        await SaveAsync().ConfigureAwait(false);
        if (provenance.InvalidReasons.IsEmpty)
        {
            foreach (var selection in CheckpointCells.Attempts)
            {
                var output = Path.Combine(artifacts, "attempts", selection.Key);
                Directory.CreateDirectory(output);
                var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
                var executable = CheckpointCampaignReadback.VerifyRuntimeFiles(artifacts, provenance);
                foreach (var argument in new[] { executable, "checkpoint-cell",
                             selection.Cell.Workload, selection.Repeat.ToString(CultureInfo.InvariantCulture), provenance.CampaignId,
                             output, Path.Combine(databases, selection.Key) })
                {
                    start.ArgumentList.Add(argument);
                }
                var outcome = await CheckpointProcess.RunAsync(start, selection.Cell.Workload,
                    CheckpointCells.CellBudget + TimeSpan.FromMinutes(2), () => ReadProgress(output), TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                captured.Add(CheckpointArchive.Capture(output, Path.Combine(artifacts, selection.Key + ".zip"), selection.Key, outcome));
                await SaveAsync().ConfigureAwait(false);
            }
        }
        return await ReadFreshAsync(artifacts).ConfigureAwait(false);

        Task SaveAsync() => CheckpointEvidence.WriteAsync(Path.Combine(artifacts, "campaign.json"),
            new CheckpointEvidence.Campaign(provenance, captured.ToImmutable()));
    }

    static async Task<int> ReadFreshAsync(string root)
    {
        try
        {
            var campaign = await CheckpointEvidence.ReadAsync<CheckpointEvidence.Campaign>(Path.Combine(root, "campaign.json")).ConfigureAwait(false);
            var executable = CheckpointCampaignReadback.VerifyRuntimeFiles(root, campaign.Provenance);
            var output = Path.Combine(root, $"readback-{Guid.NewGuid():N}.json");
            var outcome = await CheckpointSourceBuild.DotnetAsync(TimeSpan.FromMinutes(2), executable,
                "checkpoint-readback-internal", Path.GetFullPath(root), Path.GetFullPath(output)).ConfigureAwait(false);
            await CheckpointEvidence.WriteAsync(Path.Combine(root, "readback-native-outcome.json"), outcome).ConfigureAwait(false);
            var report = await CheckpointEvidence.ReadAsync<CheckpointEvidence.Readback>(output).ConfigureAwait(false);
            if (outcome.ProcessId != report.ReaderProcessId ||
                !CheckpointCampaignReadback.MatchesLoadedBuild(campaign.Provenance, report.ReaderLoadedBuild))
            {
                throw new InvalidOperationException("Actual reader PID/loaded build differs from the fresh captured source.");
            }
            await CheckpointEvidence.WriteAsync(Path.Combine(root, "readback.json"), report).ConfigureAwait(false);
            return outcome.Success && report.Complete ? 0 : 1;
        }
        catch (Exception exception)
        {
            // A local controller can retain invalid diagnostics, but cannot qualify its own drifted reader.
            var report = await CheckpointCampaignReadback.EvaluateAsync(root).ConfigureAwait(false);
            report = report with
            {
                Complete = false,
                PolicyPermitted = false,
                InvalidReasons = report.InvalidReasons.Add($"Fresh archived-source reader was unavailable/failed: {exception.Message}")
            };
            await CheckpointEvidence.WriteAsync(Path.Combine(root, "readback.json"), report).ConfigureAwait(false);
            return 1;
        }
    }

    static ulong? ReadProgress(string directory)
    {
        try
        {
            using var input = new FileStream(Path.Combine(directory, "attempt.json"), FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(input);
            return document.RootElement.GetProperty("progress").GetUInt64();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Missing/partial status never constitutes progress or resets the watchdog.
            return null;
        }
    }

    static bool Overlap(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return left.Equals(right, comparison) || left.StartsWith(right + Path.DirectorySeparatorChar, comparison) ||
               right.StartsWith(left + Path.DirectorySeparatorChar, comparison);
    }
}
