using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using Cntryl.Pants.Storage.Internal.Accounting;

namespace Cntryl.Pants.Checkpoint;

static class CheckpointCampaignReadback
{
    public static async Task<CheckpointEvidence.Readback> EvaluateAsync(string root)
    {
        root = Path.GetFullPath(root);
        var errors = ImmutableArray.CreateBuilder<string>();
        CheckpointEvidence.Campaign? campaign = null;
        try
        {
            campaign = await CheckpointEvidence.ReadAsync<CheckpointEvidence.Campaign>(Path.Combine(root, "campaign.json")).ConfigureAwait(false);
            VerifyProvenance(root, campaign.Provenance);
            if (campaign.Captured.Select(static item => item.Key).Distinct(StringComparer.Ordinal).Count() != campaign.Captured.Length ||
                campaign.Captured.Any(item => !CheckpointCells.Attempts.Any(selection => selection.Key == item.Key)))
            {
                errors.Add("Captured attempts contain duplicate or unregistered cells.");
            }
        }
        catch (Exception exception)
        {
            errors.Add($"Campaign provenance is absent/invalid: {exception.Message}");
        }
        var rows = ImmutableArray.CreateBuilder<CheckpointEvidence.AttemptVerdict>(9);
        foreach (var selection in CheckpointCells.Attempts)
        {
            var matching = campaign?.Captured.Where(item => item.Key == selection.Key).ToArray() ?? [];
            if (matching.Length != 1)
            {
                rows.Add(new CheckpointEvidence.AttemptVerdict(selection.Key, false, null,
                    ["A preregistered attempt is absent or ambiguous."], null, null));
                continue;
            }
            var captured = matching[0];
            var reasons = ImmutableArray.CreateBuilder<string>();
            CheckpointGate.Verdict? gate = null;
            if (!captured.NativeOutcome.Success)
            {
                reasons.Add($"Actual child process failed: exit={captured.NativeOutcome.ExitCode}, killed={captured.NativeOutcome.Killed}, reason={captured.NativeOutcome.TerminationReason}, start={captured.NativeOutcome.StartError}.");
            }
            try
            {
                CheckpointArchive.Verify(root, captured);
                var attempt = await CheckpointEvidence.ReadAsync<CheckpointEvidence.Attempt>(
                    Path.Combine(root, "attempts", selection.Key, "attempt.json")).ConfigureAwait(false);
                var expectedBuild = campaign!.Provenance.BuildFiles.Where(static file => file.Name.EndsWith(".dll", StringComparison.Ordinal))
                    .OrderBy(static file => file.Name, StringComparer.Ordinal).ToArray();
                Require(Equal(attempt.LoadedBuild.OrderBy(static file => file.Name, StringComparer.Ordinal).ToArray(), expectedBuild),
                    "Actual loaded child assemblies differ from the captured build/source identity.");
                gate = QualifyAttempt(attempt, selection, campaign.Provenance.CampaignId, captured.NativeOutcome);
                reasons.AddRange(gate.InvalidReasons);
            }
            catch (Exception exception)
            {
                reasons.Add($"Attempt evidence is incomplete/invalid: {exception.Message}");
            }
            var valid = reasons.Count == 0;
            rows.Add(new CheckpointEvidence.AttemptVerdict(selection.Key, valid, valid ? gate!.Miss : null,
                reasons.ToImmutable(), captured.NativeOutcome, gate));
        }
        var attempts = rows.MoveToImmutable();
        var complete = errors.Count == 0 && attempts.All(static row => row.Valid);
        var permitted = complete && CheckpointCells.Workloads.Any(cell =>
            attempts.Count(row => row.Key.StartsWith(cell.Id + "-", StringComparison.Ordinal) && row.Miss == true) >= 2);
        return new CheckpointEvidence.Readback(complete, permitted, attempts, errors.ToImmutable());
    }

    internal static CheckpointGate.Verdict QualifyAttempt(CheckpointEvidence.Attempt attempt,
        CheckpointCells.Selection selection, string campaignId, CheckpointEvidence.ProcessOutcome native)
    {
        Require(attempt.SchemaVersion == "pants-checkpoint-attempt.v1" && attempt.CampaignId == campaignId && attempt.Selection == selection && attempt.Selector == selection.Cell.Workload &&
                native.Selector == selection.Cell.Workload && native.ProcessId > 0 && native.ProcessId == attempt.ProcessId,
            "Source campaign/selector/repeat/PID binding differs.");
        Require(native.Success && attempt.Passed && attempt.TerminalError is null && attempt.FailureKind is null &&
                !attempt.Diagnostic && attempt.Verified && attempt.ReopenedVerified && attempt.Phase == "complete" &&
                !attempt.FlushInFlight && attempt.CleanupErrors.IsEmpty,
            "Actual fixed work, persistence, verification, reopen or owned cleanup failed.");
        var cell = selection.Cell;
        Require(attempt.CompletedCycles == cell.Cycles && attempt.AcknowledgedRows == checked((ulong)(cell.Cycles * cell.Rows)) &&
                attempt.MeasuredAcknowledgedRows == checked((ulong)(cell.MeasuredCycles * cell.Rows)),
            "Acknowledged rows/cycles differ from fixed construction.");
        var window = attempt.MeasuredWindow ?? throw new InvalidOperationException("Measured immutable interval is absent.");
        Require(window.AcknowledgedRows == attempt.MeasuredAcknowledgedRows && window.ElapsedNanoseconds > 0 &&
                window.FlushLatenciesNanoseconds.Length == cell.MeasuredCycles &&
                window.FlushLatenciesNanoseconds.All(static value => value > 0), "Measured flush/row/clock coverage differs.");
        Require(window.RuntimeAfter.CompactionsRun >= window.RuntimeBefore.CompactionsRun &&
                window.CompletedCompactions == checked((ulong)(window.RuntimeAfter.CompactionsRun - window.RuntimeBefore.CompactionsRun)),
            "Measured genuine compaction count differs.");
        CheckBoundary(attempt.BeforeBoundary);
        CheckBoundary(attempt.AfterBoundary);
        Require(attempt.AfterBoundary.ElapsedNanoseconds <= window.ElapsedNanoseconds,
            "End boundary capture was excluded from measurement.");
        Require(window.Before.Buckets.Where(static bucket => bucket.Medium == MetadataMedium.Persistent)
                .All(static bucket => bucket.ActiveOperations == 0), "Persistent work crosses the warmup boundary.");
        var delta = window.After.Delta(window.Before);
        Require(Equal(delta, window.Delta), "Stored interval differs from exact same-owner delta.");
        var provisional = CheckpointGate.Evaluate(delta, checked((ulong)cell.MeasuredCycles), window.CompletedCompactions);
        Require(Equal(provisional, window.ProvisionalGate), "Stored provisional gate differs from exact arithmetic.");
        var originalAfterShutdown = attempt.OriginalAfterShutdown ?? throw new InvalidOperationException("Original shutdown did not settle.");
        var original = attempt.OriginalFinal ?? throw new InvalidOperationException("Final original owner is absent.");
        var reopened = attempt.ReopenedFinal ?? throw new InvalidOperationException("Reopened shutdown did not settle.");
        _ = originalAfterShutdown.Delta(window.After);
        _ = original.Delta(originalAfterShutdown);
        var gate = CheckpointGate.Settle(provisional, delta.OwnerId, original, reopened.OwnerId, reopened);
        Require(attempt.FinalGate is not null && Equal(attempt.FinalGate, gate), "Stored final gate differs from owner settlement.");
        return gate;
    }

    static bool Equal<T>(T left, T right) =>
        System.Text.Json.JsonSerializer.Serialize(left, CheckpointEvidence.JsonOptions) ==
        System.Text.Json.JsonSerializer.Serialize(right, CheckpointEvidence.JsonOptions);

    static void CheckBoundary(CheckpointBoundary.Observation observation)
    {
        Require(observation.Complete && !observation.ProgressAdvanced && observation.FinalPersistentActive == 0 &&
                observation.Samples == observation.ActiveSamples + 1 && observation.PauseCalls == observation.ActiveSamples &&
                observation.ElapsedNanoseconds < 30_000_000_000 && observation.PausedNanoseconds <= observation.ElapsedNanoseconds &&
                observation.MaximumPauseRequestedNanoseconds <= 1_000_000,
            "Captured metadata boundary differs from the original capped policy.");
        Require(observation.PauseCalls == 0
                ? observation.PauseRequestedNanoseconds == 0 && observation.MaximumPauseRequestedNanoseconds == 0 && observation.PausedNanoseconds == 0
                : observation.MaximumPauseRequestedNanoseconds > 0 &&
                  observation.PauseRequestedNanoseconds >= observation.MaximumPauseRequestedNanoseconds &&
                  (UInt128)observation.PauseRequestedNanoseconds <= (UInt128)observation.PauseCalls * observation.MaximumPauseRequestedNanoseconds,
            "Captured pause counters differ from requested bounded sampling.");
    }

    static void VerifyProvenance(string root, CheckpointEvidence.Provenance provenance)
    {
        Require(provenance.InvalidReasons.IsEmpty && provenance.CleanSource && provenance.Commit.Length == 40 &&
                provenance.Tree.Length == 40 && provenance.Commit.All(char.IsAsciiHexDigit) && provenance.Tree.All(char.IsAsciiHexDigit),
            "Source/build capture was not qualified.");
        var archivePath = CheckpointArchive.Resolve(root, provenance.SourceArchive);
        Require(CheckpointProvenance.HashFile(archivePath) == provenance.SourceArchiveSha256, "Source archive digest differs.");
        using (var source = ZipFile.OpenRead(archivePath))
        {
            var entry = source.GetEntry("bench/Pants.Benches/packages.lock.json") ??
                        throw new InvalidOperationException("Archived package lock is absent.");
            using var input = entry.Open();
            Require(Convert.ToHexString(SHA256.HashData(input)) == provenance.LockSha256, "Archived lock identity differs.");
        }
        var build = provenance.ArchivedSourceBuild;
        Require(build?.Success == true && build.Project == CheckpointSourceBuild.BenchmarkProject &&
                build.Commit == provenance.Commit && build.SourceArchiveSha256 == provenance.SourceArchiveSha256 &&
                provenance.BuildIdentityOutcome?.Success == true,
            "Actual archived-source restore/build/identity process did not succeed.");
        _ = VerifyRuntimeFiles(root, provenance);
        Require(provenance.BuildFiles.Length == 3, "Actual engine/benchmark loaded identity is incomplete.");
        foreach (var file in provenance.BuildFiles)
        {
            Require(file.Configuration == "Release" && !file.OptimizerDisabled &&
                    file.InformationalVersion.Contains(provenance.Commit, StringComparison.OrdinalIgnoreCase),
                "Captured assembly profile/source revision differs.");
        }
    }

    public static string VerifyRuntimeFiles(string root, CheckpointEvidence.Provenance provenance)
    {
        var build = provenance.ArchivedSourceBuild ?? throw new InvalidOperationException("An actual archived-source build receipt is absent.");
        Require(build.Success && build.Commit == provenance.Commit && build.SourceArchiveSha256 == provenance.SourceArchiveSha256,
            "Archived-source build receipt differs from the selected commit/archive.");
        var executable = CheckpointArchive.Resolve(root, build.Executable!);
        var runtimeRoot = Path.GetDirectoryName(executable)!;
        var files = Directory.EnumerateFiles(runtimeRoot, "*", SearchOption.AllDirectories).Select(path =>
            Path.GetRelativePath(runtimeRoot, path).Replace(Path.DirectorySeparatorChar, '/')).ToHashSet(StringComparer.Ordinal);
        Require(files.Count == provenance.RuntimeFilesSha256.Count && files.Count > 0 &&
                files.All(provenance.RuntimeFilesSha256.ContainsKey), "Fresh build runtime/dependency file set differs.");
        foreach (var file in provenance.RuntimeFilesSha256)
        {
            Require(CheckpointProvenance.HashFile(CheckpointArchive.Resolve(runtimeRoot, file.Key)) == file.Value,
                "Fresh build runtime/dependency bytes differ.");
        }
        foreach (var file in provenance.BuildFiles)
        {
            Require(provenance.RuntimeFilesSha256.TryGetValue(file.Name, out var hash) && hash == file.Sha256,
                "Fresh loaded engine identity differs from its compiled runtime output.");
        }
        return executable;
    }

    public static bool MatchesLoadedBuild(CheckpointEvidence.Provenance provenance,
        ImmutableArray<CheckpointEvidence.BuildFile> observed) =>
        Equal(observed.OrderBy(static file => file.Name, StringComparer.Ordinal).ToArray(),
            provenance.BuildFiles.OrderBy(static file => file.Name, StringComparer.Ordinal).ToArray());

    static void Require(bool condition, string reason)
    {
        if (!condition)
        {
            throw new InvalidOperationException(reason);
        }
    }
}
