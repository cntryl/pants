using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Pants;

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
}
