using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cntryl.Pants.Observability;
using Cntryl.Pants.Storage.Internal.Accounting;

namespace Cntryl.Pants.Checkpoint;

static class CheckpointEvidence
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static async Task WriteAsync<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)).ConfigureAwait(false);
        File.Move(temporary, path, true);
    }

    public static async Task<T> ReadAsync<T>(string path) =>
        JsonSerializer.Deserialize<T>(await File.ReadAllBytesAsync(path).ConfigureAwait(false), JsonOptions)
        ?? throw new InvalidOperationException($"Evidence '{path}' is null.");

    public sealed class Attempt(string campaignId, CheckpointCells.Selection selection, bool diagnostic)
    {
        [JsonRequired]
        public string SchemaVersion { get; init; } = "pants-checkpoint-attempt.v1";
        public string CampaignId { get; init; } = campaignId;
        public CheckpointCells.Selection Selection { get; init; } = selection;
        public int ProcessId { get; init; } = Environment.ProcessId;
        public string Selector { get; init; } = selection.Cell.Workload;
        [JsonRequired]
        public ImmutableArray<BuildFile> LoadedBuild { get; set; } = [];
        public bool Diagnostic { get; init; } = diagnostic;
        public string Phase { get; set; } = "setup";
        public bool Passed { get; set; }
        public string? TerminalError { get; set; }
        public string? FailureKind { get; set; }
        public bool Verified { get; set; }
        public bool ReopenedVerified { get; set; }
        public bool FlushInFlight { get; set; }
        public ulong AcknowledgedRows { get; set; }
        public ulong MeasuredAcknowledgedRows { get; set; }
        public int CompletedCycles { get; set; }
        public ulong Progress { get; set; }
        public long CellStarted { get; init; }
        public long CellDeadline { get; init; }
        public string? DatabasePath { get; set; }
        public Window? MeasuredWindow { get; set; }
        public MetadataAccountingSnapshot? OriginalAfterShutdown { get; set; }
        public MetadataAccountingSnapshot? OriginalFinal { get; set; }
        public MetadataAccountingSnapshot? ReopenedFinal { get; set; }
        public CheckpointGate.Verdict? FinalGate { get; set; }
        public CheckpointBoundary.Observation BeforeBoundary { get; init; } = new();
        public CheckpointBoundary.Observation AfterBoundary { get; init; } = new();
        public CheckpointCommitPolicy.Observation CommitBackpressure { get; init; } = new();
        public bool DeviceWriteAmplificationMeasured { get; init; }
        public string ActiveOperationsScope { get; init; } = "metadata only; not a global runtime idle barrier";
        public string NativeTrust { get; init; } = "actual local process and delegated persistent I/O; no hosted or statistical attestation";
        public ImmutableArray<string> CleanupErrors { get; set; } = [];
    }

    public sealed record Window(MetadataAccountingSnapshot Before, MetadataAccountingSnapshot After,
        MetadataAccountingSnapshot Delta, PantsRuntimeMetrics RuntimeBefore, PantsRuntimeMetrics RuntimeAfter,
        ulong ElapsedNanoseconds, ulong AcknowledgedRows, ulong CompletedCompactions,
        ImmutableArray<ulong> FlushLatenciesNanoseconds, CheckpointGate.Verdict ProvisionalGate);

    public sealed record ProcessOutcome(int ProcessId, string Selector, int ExitCode, bool Killed,
        string StandardOutput, string StandardError, string? StartError, ulong ElapsedNanoseconds)
    {
        public string? TerminationReason { get; init; }
        public bool Success => StartError is null && TerminationReason is null && !Killed && ExitCode == 0;
    }

    public sealed record CapturedAttempt(string Key, ProcessOutcome NativeOutcome, string Archive,
        string ArchiveSha256, ImmutableDictionary<string, string> ContentsSha256);

    public sealed record BuildFile(string Name, string Sha256, string InformationalVersion, string Configuration, bool OptimizerDisabled, Guid ModuleVersionId);

    public sealed record Provenance(string CampaignId, string SourceRoot, string Commit, string Tree,
        bool CleanSource, string SourceArchive, string SourceArchiveSha256, string LockSha256,
        ImmutableArray<BuildFile> BuildFiles, string Runtime, string OperatingSystem, string Architecture,
        string CommandLine, ImmutableArray<string> InvalidReasons)
    {
        public CheckpointSourceBuild.Build? ArchivedSourceBuild { get; init; }
        public ProcessOutcome? BuildIdentityOutcome { get; init; }
        public ImmutableDictionary<string, string> RuntimeFilesSha256 { get; init; } = ImmutableDictionary<string, string>.Empty;
        public string TrustClass { get; init; } = "captured-local-archive-compiled-build-process; no hosted attestation";
    }

    public sealed record Campaign(Provenance Provenance, ImmutableArray<CapturedAttempt> Captured);

    public sealed record AttemptVerdict(string Key, bool Valid, bool? Miss, ImmutableArray<string> InvalidReasons,
        ProcessOutcome? NativeOutcome, CheckpointGate.Verdict? AccountingGate);

    public sealed record Readback(bool Complete, bool PolicyPermitted, ImmutableArray<AttemptVerdict> Attempts,
        ImmutableArray<string> InvalidReasons)
    {
        public string SchemaVersion { get; init; } = "pants-checkpoint-campaign-readback.v1";
        public int ReaderProcessId { get; init; }
        public ImmutableArray<BuildFile> ReaderLoadedBuild { get; init; } = [];
        public bool DeviceWriteAmplificationMeasured { get; init; }
        public string EvidenceBoundary { get; init; } = "local captured source/build/process/archive, not hosted release or performance parity";
    }
}
