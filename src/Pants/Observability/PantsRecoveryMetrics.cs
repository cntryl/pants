namespace Cntryl.Pants.Observability;

public sealed record PantsRecoveryMetrics(
    long WalRecordsReplayed,
    long WalBytesReplayed,
    long IntentLogReplayRuns,
    long IntentLogEntriesReplayed);
