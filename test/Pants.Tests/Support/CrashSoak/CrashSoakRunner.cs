using System.Diagnostics;
using System.Globalization;
using System.Text;
using Cntryl.Pants.Support.TestDoubles;
using Xunit.Sdk;

namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>
///     Drives one seed: for each cycle it launches a child that runs the cycle's workload until it is
///     killed, takes the dead writer's lease over by reopening with strict recovery, checks the
///     recovered state against the child's ledger, and adopts that state as the next cycle's floor.
/// </summary>
sealed class CrashSoakRunner(Type childTestClass, string childTestName)
{
    static readonly TimeSpan ChildTimeout = TestTimeouts.Expected * 2;
    static readonly TimeSpan ReopenTimeout = TestTimeouts.Expected;

    public async Task<CrashSoakReport> RunAsync(CrashSoakScenario scenario, int cycles)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentOutOfRangeException.ThrowIfLessThan(cycles, 1);
        if (scenario.Backend == CrashSoakBackend.Sqrzl)
        {
            await CrashSoakSqrzl.RequireAsync();
        }

        using var directory = new TemporaryDirectory();
        var prefix = scenario.Backend == CrashSoakBackend.Local
            ? string.Empty
            : $"crash-soak/{Guid.NewGuid():N}";
        var model = new CrashSoakModel(scenario);
        var report = new CrashSoakReport(scenario);
        for (var index = 0; index < cycles; index++)
        {
            var started = Stopwatch.StartNew();
            var cycle = CrashSoakWorkload.CreateCycle(scenario, index);
            model.Include(cycle);
            var spec = new CrashSoakChildSpec(
                scenario.Seed,
                scenario.Backend,
                scenario.OperationsPerCycle,
                index,
                directory.Path,
                prefix);
            var ledger = await RunChildAsync(spec, cycle, cycles);
            if (CrashSoakSettings.KeepFailures)
            {
                CopyDirectory(
                    CrashSoakStorage.DatabasePath(directory.Path),
                    Path.Combine(directory.Path, string.Create(CultureInfo.InvariantCulture, $"killed-{index:000}")));
            }

            if (cycle.CrashPlan.LoseLocalCache)
            {
                CrashSoakStorage.DiscardLocalCache(scenario.Backend, directory.Path);
            }

            var (observed, scanned) = await ReopenAndReadAsync(spec, model.Keys, cycles, cycle);
            var violations = model.Verify(cycle, ledger, cycle.CrashPlan.LoseLocalCache, observed, scanned);
            violations.AddRange(ledger.Errors.Select(static error => $"the child hit an unexpected exception: {error}"));
            if (violations.Count > 0)
            {
                throw new XunitException(Describe(
                    spec,
                    cycles,
                    cycle,
                    "the recovered state contradicts the ledger",
                    ledger,
                    violations,
                    PreserveForInspection(directory.Path, scenario.Seed)));
            }

            model.Adopt(observed);
            report.Record(cycle, ledger, started.Elapsed);
        }

        return report;
    }

    async Task<CrashSoakLedger> RunChildAsync(CrashSoakChildSpec spec, CrashSoakCycle cycle, int cycles)
    {
        var ledgerPath = CrashSoakStorage.LedgerPath(spec.Root, spec.Cycle);
        string? abortedLaunch = null;
        while (true)
        {
            var start = CrashChildProcess.CreateStartInfo(childTestClass, childTestName);
            spec.ApplyTo(start);
            using var child = CrashChildProcess.Start(start, "crash/soak child");
            using (var timeout = new CancellationTokenSource(ChildTimeout))
            {
                try
                {
                    await child.Process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    child.TryKillProcessTree();
                    await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    throw new XunitException(Describe(
                        spec,
                        cycles,
                        cycle,
                        $"the child did not die within {ChildTimeout.TotalSeconds:0}s: {await child.DescribeAsync()}",
                        File.Exists(ledgerPath) ? CrashSoakLedger.Read(ledgerPath) : null,
                        [],
                        null));
                }
            }

            if (!File.Exists(ledgerPath) && abortedLaunch is null)
            {
                // The launcher can die before the test host runs any harness code (see
                // CrashChildProcess); the database is untouched then, so one relaunch is safe.
                abortedLaunch = await child.DescribeAsync();
                continue;
            }

            var ledger = File.Exists(ledgerPath) ? CrashSoakLedger.Read(ledgerPath) : null;
            if (ledger?.KillReason is null)
            {
                throw new XunitException(Describe(
                    spec,
                    cycles,
                    cycle,
                    $"the child exited without reaching its kill point: {await child.DescribeAsync()}",
                    ledger,
                    [],
                    null));
            }

            return ledger;
        }
    }

    static async Task<(Dictionary<string, string> Observed, List<KeyValuePair<string, string>> Scanned)>
        ReopenAndReadAsync(CrashSoakChildSpec spec, IReadOnlyCollection<string> keys, int cycles, CrashSoakCycle cycle)
    {
        var scenario = spec.Scenario;
        var database = await OpenAsync(spec, cycles, cycle);
        try
        {
            var observed = new Dictionary<string, string>(StringComparer.Ordinal);
            var scanned = new List<KeyValuePair<string, string>>();
            await using (var reader = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadOnly))
            {
                foreach (var key in keys)
                {
                    if (await reader.GetAsync(TestBytes.FromString(key)) is { } value)
                    {
                        observed[key] = TestBytes.ToText(value);
                    }
                }

                await using var scan = await reader.ScanAsync(new PantsScanQuery());
                await foreach (var entry in scan)
                {
                    scanned.Add(new KeyValuePair<string, string>(
                        TestBytes.ToText(entry.Key),
                        TestBytes.ToText(entry.Value)));
                }
            }

            if (scenario.Backend != CrashSoakBackend.Local)
            {
                // The next cycle may lose its cache, so make the state just verified cloud-durable:
                // an empty CloudStrict commit seals and publishes everything recovery replayed.
                await using var confirmation = await database.Transactions.BeginAsync(
                    database.ColumnFamilies.DefaultFamily,
                    PantsTransactionMode.ReadWrite);
                await confirmation.CommitAsync(PantsWriteOptions.CloudStrict);
            }

            await database.ShutdownAsync(TestTimeouts.Expected);
            return (observed, scanned);
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    static async Task<IPantsDatabase> OpenAsync(CrashSoakChildSpec spec, int cycles, CrashSoakCycle cycle)
    {
        var options = CrashSoakStorage.CreateOptions(spec.Scenario, spec.Root, spec.CloudPrefix);
        var dependencies = CrashSoakStorage.CreateDependencies(CrashSoakStorage.VerifierGeneration(spec.Cycle));
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return await PantsDatabase.OpenForTestingAsync(options, dependencies);
            }
            catch (PantsLeaseHeldException) when (deadline.Elapsed < ReopenTimeout)
            {
                // The operating system may release the dead child's LOCK handle a moment after exit.
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }
            catch (PantsException exception)
            {
                throw new XunitException(
                    Describe(
                        spec,
                        cycles,
                        cycle,
                        $"strict reopen after the kill failed: {exception.GetType().Name}: {exception.Message}",
                        CrashSoakLedger.Read(CrashSoakStorage.LedgerPath(spec.Root, spec.Cycle)),
                        [],
                        PreserveForInspection(spec.Root, spec.Seed)),
                    exception);
            }
        }
    }

    static string Describe(
        CrashSoakChildSpec spec,
        int cycles,
        CrashSoakCycle cycle,
        string problem,
        CrashSoakLedger? ledger,
        List<string> violations,
        string? preservedAt)
    {
        var message = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"Crash/soak seed {spec.Seed} ({spec.Scenario}) failed in cycle {cycle.Index}: {problem}.")
            .AppendLine()
            .Append(CultureInfo.InvariantCulture, $"Crash plan: {cycle.CrashPlan}; child kill: {ledger?.KillReason ?? "<none recorded>"}.")
            .AppendLine();
        if (ledger is not null)
        {
            message
                .Append(CultureInfo.InvariantCulture, $"Ledger: acked={ledger.Count(CrashSoakOutcome.Acked)} ")
                .Append(CultureInfo.InvariantCulture, $"failed={ledger.Count(CrashSoakOutcome.Failed)} ")
                .Append(CultureInfo.InvariantCulture, $"unknown={ledger.Count(CrashSoakOutcome.Unknown)}.")
                .AppendLine();
        }

        foreach (var violation in violations.Take(25))
        {
            message.Append("  - ").AppendLine(violation);
        }

        if (violations.Count > 25)
        {
            message.Append(CultureInfo.InvariantCulture, $"  … and {violations.Count - 25} more.").AppendLine();
        }

        if (preservedAt is not null)
        {
            message.Append(CultureInfo.InvariantCulture, $"Database and ledgers preserved at {preservedAt}.").AppendLine();
        }

        return message
            .Append(CultureInfo.InvariantCulture, $"Replay: {CrashSoakSettings.ReplayCommand(spec.Seed, spec.Backend, cycles, spec.OperationsPerCycle)}")
            .ToString();
    }

    /// <summary>
    ///     Copies the run to a directory that outlives the test when
    ///     <see cref="CrashSoakSettings.KeepFailuresVariable" /> is set; each <c>killed-NNN</c> copy is
    ///     the database exactly as that cycle's kill left it, before any reopen.
    /// </summary>
    static string? PreserveForInspection(string root, int seed)
    {
        if (!CrashSoakSettings.KeepFailures)
        {
            return null;
        }

        var target = Path.Combine(
            Path.GetTempPath(),
            string.Create(CultureInfo.InvariantCulture, $"pants-crash-soak-{seed}-{Guid.NewGuid():N}"));
        CopyDirectory(root, target);
        return target;
    }

    static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}
