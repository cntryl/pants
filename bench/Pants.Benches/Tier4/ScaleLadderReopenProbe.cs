using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Cntryl.Pants.Storage;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Tier4;

/// <summary>
///     Measures peak RSS for a process that does nothing but open an already-populated database and
///     read a few values back — run as a genuinely separate child process so the reading is not
///     contaminated by the ingest/compaction process's own memory history (which never shrinks back
///     to a clean baseline just because a GC ran). This is the "startup RSS for the populated
///     corpus" figure the report publishes; the in-process reopen check in
///     <see cref="ScaleLadderRunner" /> stays as a cheap same-process sanity signal alongside it.
/// </summary>
static class ScaleLadderReopenProbe
{
    public static async Task<(
        bool Success,
        long PeakRssBytes,
        long ConfiguredBudgetBytes,
        string Detail)> RunAsync(
        string databasePath,
        long recordCount,
        long budgetBytes)
    {
        var resultsPath = Path.Combine(Path.GetTempPath(), $"pants-reopen-probe-{Guid.NewGuid():N}.txt");
        try
        {
            var start = ScaleLadderChildProcess.Create(
                "scaleladder-reopen-probe-child",
                databasePath,
                recordCount.ToString(CultureInfo.InvariantCulture),
                budgetBytes.ToString(CultureInfo.InvariantCulture),
                resultsPath);

            using var child = Process.Start(start) ??
                              throw new InvalidOperationException("Could not start the reopen probe child.");
            await using var peakMonitor = new PeakWorkingSetMonitor(child);
            var standardError = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                await child.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(child);
                await child.WaitForExitAsync(CancellationToken.None);
                await peakMonitor.StopAsync();
                return (false, peakMonitor.PeakBytes, 0, "Reopen probe child did not exit within 120 seconds.");
            }

            if (child.ExitCode != 0 || !File.Exists(resultsPath))
            {
                await peakMonitor.StopAsync();
                return (false, peakMonitor.PeakBytes, 0, $"Reopen probe child failed: exit={child.ExitCode}; " +
                                                         $"stderr={await standardError}");
            }

            var configuredBudgetBytes = long.Parse(
                await File.ReadAllTextAsync(resultsPath),
                CultureInfo.InvariantCulture);
            if (configuredBudgetBytes != budgetBytes)
            {
                await peakMonitor.StopAsync();
                return (
                    false,
                    peakMonitor.PeakBytes,
                    configuredBudgetBytes,
                    $"Reopen probe used {configuredBudgetBytes} bytes instead of the requested " +
                    $"{budgetBytes}-byte tier budget.");
            }

            await peakMonitor.StopAsync();
            return (
                true,
                peakMonitor.PeakBytes,
                configuredBudgetBytes,
                "populated-corpus open verified and peak RSS sampled");
        }
        finally
        {
            File.Delete(resultsPath);
        }
    }

    public static async Task RunChildAsync(
        string databasePath,
        long recordCount,
        long budgetBytes,
        string resultsPath)
    {
        var options = PantsOpenOptions.Local(databasePath)
            .WithBackgroundCompaction(false)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(budgetBytes));
        await using var database = await PantsDatabase.OpenAsync(options);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        foreach (var index in new[] { 0L, recordCount / 2, Math.Max(0, recordCount - 1) })
        {
            foreach (var mutation in ScaleLadderRunner.CreateMutationsForRecord(index))
            {
                var value = await reader.GetAsync(mutation.Key);
                if (value is null || !value.Value.Span.SequenceEqual(mutation.Value))
                {
                    throw new InvalidOperationException(
                        $"Reopen probe spot-check for record {index} failed.");
                }
            }
        }

        await File.WriteAllTextAsync(
            resultsPath,
            (options.Memory.Budget.Bytes ?? throw new InvalidOperationException(
                "The scale ladder requires an explicit memory budget."))
            .ToString(CultureInfo.InvariantCulture));
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
        }
        catch (Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
    }
}
