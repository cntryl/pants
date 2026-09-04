using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Cntryl.Pants.Observability;

public static class PantsDiagnostics
{
    public static ActivitySource ActivitySource { get; } = new("Pants", "1.0");

    public static Meter Meter { get; } = new("Pants", "1.0");

    internal static Counter<long> TransactionsStarted { get; } =
        Meter.CreateCounter<long>("pants.transactions.started");

    internal static Counter<long> TransactionsCommitted { get; } =
        Meter.CreateCounter<long>("pants.transactions.committed");

    internal static Counter<long> TransactionsRolledBack { get; } =
        Meter.CreateCounter<long>("pants.transactions.rolledback");

    internal static Counter<long> TransactionsConflicted { get; } =
        Meter.CreateCounter<long>("pants.transactions.conflicted");

    internal static Counter<long> CommandsRejected { get; } =
        Meter.CreateCounter<long>("pants.runtime.commands_rejected");

    internal static Histogram<double> CommandLatencyMilliseconds { get; } =
        Meter.CreateHistogram<double>("pants.runtime.command_latency_ms", "ms");

    internal static Counter<long> WalAppends { get; } =
        Meter.CreateCounter<long>("pants.wal.appends");

    internal static Counter<long> FlushPublications { get; } =
        Meter.CreateCounter<long>("pants.flush.publications");

    internal static Counter<long> CompactionsCompleted { get; } =
        Meter.CreateCounter<long>("pants.compactions.completed");

    internal static Counter<long> CompactionsFailed { get; } =
        Meter.CreateCounter<long>("pants.compactions.failed");

    internal static Counter<long> CloudWalUploadsCompleted { get; } =
        Meter.CreateCounter<long>("pants.cloud.wal_uploads.completed");

    internal static Counter<long> CloudFlushRetries { get; } =
        Meter.CreateCounter<long>("pants.cloud.flush_retries");

    internal static Counter<long> Reads { get; } =
        Meter.CreateCounter<long>("pants.reads");

    internal static Counter<long> WalRecoveryRecords { get; } =
        Meter.CreateCounter<long>("pants.recovery.wal_records");
}
