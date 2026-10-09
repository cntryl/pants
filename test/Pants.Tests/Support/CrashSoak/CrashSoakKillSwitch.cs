using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>
///     Kills the child process at the cycle's planned point: inside an engine failpoint, right after a
///     commit is dispatched, or after the workload. The kill is a SIGKILL-equivalent, so nothing the
///     engine has not already handed to the operating system survives it.
/// </summary>
sealed class CrashSoakKillSwitch(CrashSoakLedgerWriter ledger, CrashSoakCrashPlan plan) : IFailpointHandler
{
    int _failpointHits;
    int _dispatches;

    public void Hit(Failpoint failpoint)
    {
        if (plan.Kind == CrashSoakCrashKind.Failpoint &&
            failpoint == plan.Failpoint &&
            Interlocked.Increment(ref _failpointHits) == plan.Count)
        {
            Kill($"hit {plan.Count} of {failpoint}");
        }
    }

    /// <summary>Counts one dispatched commit and reports whether the plan kills the child now.</summary>
    public bool ShouldKillAfterDispatch() =>
        plan.Kind == CrashSoakCrashKind.AfterDispatch &&
        Interlocked.Increment(ref _dispatches) == plan.Count;

    [DoesNotReturn]
    public void Kill(string reason)
    {
        ledger.Killing(reason);
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Kill();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception
                                              or NotSupportedException)
        {
            // FailFast below still ends the process without running any shutdown path.
        }

        Environment.FailFast($"Crash/soak child killed: {reason}");
    }
}
