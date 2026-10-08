namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>What a passing run exercised, so a caller can tell a vacuous run from a useful one.</summary>
sealed class CrashSoakReport(CrashSoakScenario scenario)
{
    readonly List<string> _cycles = [];

    public CrashSoakScenario Scenario { get; } = scenario;

    public int Acked { get; private set; }

    public int Failed { get; private set; }

    public int Unknown { get; private set; }

    public int NotDispatched { get; private set; }

    public IReadOnlyList<string> Cycles => _cycles;

    public void Record(CrashSoakCycle cycle, CrashSoakLedger ledger, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(ledger);
        var operations = cycle.Operations.ToArray();
        var acked = operations.Count(operation => ledger.OutcomeOf(operation) == CrashSoakOutcome.Acked);
        var failed = operations.Count(operation => ledger.OutcomeOf(operation) == CrashSoakOutcome.Failed);
        var unknown = operations.Count(operation => ledger.OutcomeOf(operation) == CrashSoakOutcome.Unknown);
        var notDispatched = operations.Length - acked - failed - unknown;
        Acked += acked;
        Failed += failed;
        Unknown += unknown;
        NotDispatched += notDispatched;
        _cycles.Add(
            $"cycle {cycle.Index}: plan '{cycle.CrashPlan}', killed '{ledger.KillReason}', " +
            $"acked={acked} failed={failed} unknown={unknown} notDispatched={notDispatched}, " +
            $"{elapsed.TotalMilliseconds:0}ms");
    }

    public override string ToString() =>
        $"{Scenario}: acked={Acked} failed={Failed} unknown={Unknown} notDispatched={NotDispatched}" +
        Environment.NewLine + string.Join(Environment.NewLine, _cycles);
}
