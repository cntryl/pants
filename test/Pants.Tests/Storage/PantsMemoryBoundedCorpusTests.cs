using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Xunit.Sdk;

namespace Cntryl.Pants.Tests.Storage;

/// <summary>
/// Slice 6d (issue #219): a subprocess resource regression proving open/steady-state RSS for a
/// local, non-hybrid database does not scale with corpus size the way it did before this issue's
/// work — built on the same child-process convention as the crash-recovery tests (e.g.
/// <c>PantsCommitCoalescingCrashRecoveryTests</c>): the same test assembly is re-invoked via
/// <c>dotnet vstest</c> targeting <see cref="MeasureRssChild"/>, coordinated by environment
/// variables, except the child here runs to completion and reports results via a file instead of
/// being crashed mid-flight.
///
/// Two checks, both against a single pair of child runs so total subprocess-spawn overhead stays
/// bounded to two children:
/// 1. An absolute allowance check (the acceptance criteria's literal wording): open/steady-state
///    RSS for a corpus 4x the configured memory budget stays within a documented fixed
///    allowance over that budget.
/// 2. A relative-scaling check, empirically calibrated against this codebase (see the constants
///    below): RSS for a corpus 16x the budget must not have grown anywhere near proportionally
///    to the ~4x larger corpus compared to the 4x run — this is the stronger, less
///    machine-dependent proof that memory tracks working set, not corpus size. Measured directly
///    during this slice's development: a 16 MiB budget with a 64 MiB corpus (4x) peaked at ~95
///    MiB RSS; the SAME budget with a 256 MiB corpus (16x — four times more data) peaked at only
///    ~107 MiB — a ~12 MiB difference for a ~192 MiB larger corpus.
/// </summary>
[Collection(CrashProcessTestGroup.Name)]
public sealed class PantsMemoryBoundedCorpusTests
{
    const string ChildRoleEnvironmentVariable = "PANTS_RSS_CHILD_ROLE";
    const string ChildRole = "measure";
    const string DatabasePathEnvironmentVariable = "PANTS_RSS_DATABASE_PATH";
    const string BudgetBytesEnvironmentVariable = "PANTS_RSS_BUDGET_BYTES";
    const string CorpusMultiplierEnvironmentVariable = "PANTS_RSS_CORPUS_MULTIPLIER";
    const string ResultsPathEnvironmentVariable = "PANTS_RSS_RESULTS_PATH";
    const long BudgetBytes = 16L * 1024 * 1024;
    const int SmallMultiplier = 4;
    const int LargeMultiplier = 16;

    // Documented, deliberately generous fixed allowance over the configured budget — .NET
    // process RSS carries substantial baseline overhead (CLR/JIT/GC segment reservation, thread
    // pool growth) independent of anything this engine does; observed baseline-before-any-open
    // RSS alone was already ~38 MiB on the development machine. This threshold exists to catch a
    // real regression (RSS materially tracking corpus size again), not to assert a tight bound —
    // the relative-scaling check below is the more sensitive signal.
    const long FixedAllowanceBytes = 300L * 1024 * 1024;

    // The 16x run's RSS must not exceed the 4x run's RSS by more than this fraction of the extra
    // corpus bytes ingested (16x - 4x = 12x the budget) — i.e. growth must look like bounded
    // working-set overhead, not like the ~1:1 "every byte ingested becomes a resident byte"
    // relationship a full in-memory-resident engine would show.
    const double MaximumRssGrowthFractionOfExtraCorpus = 0.25;

    [Fact]
    public async Task MeasureRssChild()
    {
        if (!StringComparer.Ordinal.Equals(
                Environment.GetEnvironmentVariable(ChildRoleEnvironmentVariable),
                ChildRole))
        {
            return;
        }

        var databasePath = Assert.IsType<string>(
            Environment.GetEnvironmentVariable(DatabasePathEnvironmentVariable));
        var budgetBytes = long.Parse(
            Assert.IsType<string>(Environment.GetEnvironmentVariable(BudgetBytesEnvironmentVariable)),
            CultureInfo.InvariantCulture);
        var multiplier = int.Parse(
            Assert.IsType<string>(Environment.GetEnvironmentVariable(CorpusMultiplierEnvironmentVariable)),
            CultureInfo.InvariantCulture);
        var resultsPath = Assert.IsType<string>(
            Environment.GetEnvironmentVariable(ResultsPathEnvironmentVariable));

        await RunChildAsync(databasePath, budgetBytes, multiplier, resultsPath);
    }

    [Fact]
    public async Task ShouldKeepRssBoundedAndNotProportionalToCorpusSizeForALocalDatabase()
    {
        using var smallDirectory = new TemporaryDirectory();
        using var largeDirectory = new TemporaryDirectory();
        var smallResults = await RunMeasurementChildAsync(
            smallDirectory.Path,
            BudgetBytes,
            SmallMultiplier);
        var largeResults = await RunMeasurementChildAsync(
            largeDirectory.Path,
            BudgetBytes,
            LargeMultiplier);

        // 1. Absolute allowance — the acceptance criteria's literal wording.
        AssertWithinAllowance(smallResults, BudgetBytes, FixedAllowanceBytes);
        AssertWithinAllowance(largeResults, BudgetBytes, FixedAllowanceBytes);

        // 2. Relative scaling — the stronger signal. The large run ingested
        // (LargeMultiplier - SmallMultiplier) * BudgetBytes more data than the small run; its
        // steady-state RSS must not have grown anywhere near proportionally to that.
        var extraCorpusBytes = checked((LargeMultiplier - SmallMultiplier) * BudgetBytes);
        var rssGrowth = largeResults.SteadyStateRssBytes - smallResults.SteadyStateRssBytes;
        Assert.True(
            rssGrowth < extraCorpusBytes * MaximumRssGrowthFractionOfExtraCorpus,
            $"Steady-state RSS grew by {rssGrowth / 1024 / 1024} MiB for {extraCorpusBytes / 1024 / 1024} " +
            $"MiB of extra corpus ({SmallMultiplier}x to {LargeMultiplier}x budget) — expected well " +
            "under a proportional relationship. small=" + Describe(smallResults) +
            " large=" + Describe(largeResults));
    }

    static void AssertWithinAllowance(RssResults results, long budgetBytes, long allowanceBytes)
    {
        var limit = checked(budgetBytes + allowanceBytes);
        Assert.True(
            results.OpenRssBytes <= limit,
            $"Open RSS {results.OpenRssBytes / 1024 / 1024} MiB exceeded {limit / 1024 / 1024} MiB " +
            $"(budget {budgetBytes / 1024 / 1024} MiB + allowance {allowanceBytes / 1024 / 1024} MiB).");
        Assert.True(
            results.SteadyStateRssBytes <= limit,
            $"Steady-state RSS {results.SteadyStateRssBytes / 1024 / 1024} MiB exceeded " +
            $"{limit / 1024 / 1024} MiB (budget {budgetBytes / 1024 / 1024} MiB + allowance " +
            $"{allowanceBytes / 1024 / 1024} MiB).");
        Assert.True(
            results.ReopenRssBytes <= limit,
            $"Clean-reopen RSS {results.ReopenRssBytes / 1024 / 1024} MiB exceeded " +
            $"{limit / 1024 / 1024} MiB (budget {budgetBytes / 1024 / 1024} MiB + allowance " +
            $"{allowanceBytes / 1024 / 1024} MiB).");
    }

    static string Describe(RssResults results) =>
        $"(open={results.OpenRssBytes / 1024 / 1024}MiB, " +
        $"steady={results.SteadyStateRssBytes / 1024 / 1024}MiB, " +
        $"reopen={results.ReopenRssBytes / 1024 / 1024}MiB)";

    static async Task<RssResults> RunMeasurementChildAsync(
        string databasePath,
        long budgetBytes,
        int multiplier)
    {
        var resultsPath = Path.Combine(
            Path.GetTempPath(),
            $"pants-rss-results-{Guid.NewGuid():N}.txt");
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
                           Environment.ProcessPath ??
                           "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(PantsMemoryBoundedCorpusTests).Assembly.Location);
            start.ArgumentList.Add($"/Platform:{RuntimeInformation.ProcessArchitecture}");
            start.ArgumentList.Add(
                $"--Tests:{typeof(PantsMemoryBoundedCorpusTests).FullName}.{nameof(MeasureRssChild)}");
            start.Environment[ChildRoleEnvironmentVariable] = ChildRole;
            start.Environment[DatabasePathEnvironmentVariable] = databasePath;
            start.Environment[BudgetBytesEnvironmentVariable] =
                budgetBytes.ToString(CultureInfo.InvariantCulture);
            start.Environment[CorpusMultiplierEnvironmentVariable] =
                multiplier.ToString(CultureInfo.InvariantCulture);
            start.Environment[ResultsPathEnvironmentVariable] = resultsPath;

            using var child = Process.Start(start) ??
                               throw new InvalidOperationException(
                                   "Could not start the RSS-measurement child.");
            var standardOutput = child.StandardOutput.ReadToEndAsync();
            var standardError = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(240));
            try
            {
                await child.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
            {
                TryKillProcessTree(child);
                await child.WaitForExitAsync(CancellationToken.None);
                throw new XunitException(
                    "RSS-measurement child did not exit within 240 seconds.",
                    exception);
            }

            Assert.True(
                child.ExitCode == 0 && File.Exists(resultsPath),
                $"RSS-measurement child (budget={budgetBytes}, multiplier={multiplier}) failed: " +
                $"exit={child.ExitCode}; results-exists={File.Exists(resultsPath)}; " +
                $"stdout={await standardOutput}; stderr={await standardError}");

            return RssResults.Parse(await File.ReadAllTextAsync(resultsPath));
        }
        finally
        {
            File.Delete(resultsPath);
        }
    }

    static async Task RunChildAsync(
        string databasePath,
        long budgetBytes,
        int multiplier,
        string resultsPath)
    {
        var targetBytes = checked(budgetBytes * multiplier);
        var options = PantsOpenOptions.Local(databasePath)
            .WithBackgroundCompaction(false)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budgetBytes));

        long openRss;
        long steadyStateRss;
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            openRss = MeasureRssBytes();

            var value = new byte[4096];
            long written = 0;
            var index = 0;
            while (written < targetBytes)
            {
                await using (var transaction = await database.BeginTransactionAsync(
                                 database.DefaultColumnFamily,
                                 PantsTransactionMode.ReadWrite))
                {
                    transaction.Put(
                        System.Text.Encoding.UTF8.GetBytes($"key-{index:D8}"),
                        value);
                    await transaction.CommitAsync(PantsWriteOptions.Buffered);
                }

                written += value.Length;
                index++;
                if (index % 50 == 0)
                {
                    await database.FlushAsync(database.DefaultColumnFamily);
                }
            }

            await database.FlushAsync(database.DefaultColumnFamily);

            // Prove the corpus is actually correctly readable, not merely written — a handful of
            // spot checks across the key range, not exhaustive (this is a resource test, not a
            // correctness one; correctness of disk-resident reads is covered extensively
            // elsewhere, e.g. PantsPointReadDiskResidentTests).
            await using var reader = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadOnly);
            foreach (var spotCheckIndex in new[] { 0, index / 2, index - 1 })
            {
                var read = await reader.GetAsync(
                    System.Text.Encoding.UTF8.GetBytes($"key-{spotCheckIndex:D8}"));
                if (read is null)
                {
                    throw new InvalidOperationException(
                        $"Spot-check key-{spotCheckIndex:D8} was not found after ingest.");
                }
            }

            steadyStateRss = MeasureRssBytes();
        }

        long reopenRss;
        await using (var reopened = await PantsDatabase.OpenAsync(options))
        {
            reopenRss = MeasureRssBytes();

            // The RSS numbers above are meaningless if the disk-resident recovery path merely
            // opens without actually being able to serve the corpus back — verify the same spot
            // checks survive a full close-and-reopen, not only measure memory around it.
            await using var reopenedReader = await reopened.BeginTransactionAsync(
                reopened.DefaultColumnFamily,
                PantsTransactionMode.ReadOnly);
            var recordCount = checked((int)(budgetBytes * multiplier / 4096));
            foreach (var spotCheckIndex in new[] { 0, recordCount / 2, recordCount - 1 })
            {
                var read = await reopenedReader.GetAsync(
                    System.Text.Encoding.UTF8.GetBytes($"key-{spotCheckIndex:D8}"));
                if (read is null || read.Value.Length != 4096)
                {
                    throw new InvalidOperationException(
                        $"Spot-check key-{spotCheckIndex:D8} was not correctly recovered after reopen.");
                }
            }
        }

        await File.WriteAllTextAsync(
            resultsPath,
            new RssResults(openRss, steadyStateRss, reopenRss).Serialize());
    }

    static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited after HasExited was observed.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best effort — nothing more to do if the OS refuses the kill.
        }
        catch (NotSupportedException)
        {
            // Best effort — nothing more to do if the platform refuses the kill.
        }
    }

    static long MeasureRssBytes()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.WorkingSet64;
    }

    sealed record RssResults(long OpenRssBytes, long SteadyStateRssBytes, long ReopenRssBytes)
    {
        public string Serialize() =>
            $"open={OpenRssBytes}\nsteady={SteadyStateRssBytes}\nreopen={ReopenRssBytes}\n";

        public static RssResults Parse(string content)
        {
            var values = content
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => line.Split('=', 2))
                .ToDictionary(static parts => parts[0], static parts => long.Parse(
                    parts[1],
                    CultureInfo.InvariantCulture));
            return new RssResults(values["open"], values["steady"], values["reopen"]);
        }
    }
}
