using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Tier4;

/// <summary>
///     The crash/WAL-replay-recovery check the scale-ladder report advertises: a genuinely separate
///     child process durably commits a batch of records (<see cref="PantsWriteOptions.Sync" />, so
///     the WAL write is acknowledged before this process could plausibly be considered "done" with
///     it), signals readiness, and is then killed abruptly by the parent — an actual crash boundary,
///     not merely a clean shutdown followed by reopen. The parent then reopens the same database
///     fresh and verifies every committed record recovered correctly through WAL replay.
/// </summary>
static class ScaleLadderCrashCheck
{
    const int RecordCount = 2_000;
    const string ReadyMarkerFileName = "crash-check.ready";

    public static async Task<(bool Success, string Detail)> RunAsync(int recordCount = RecordCount)
    {
        using var directory = new TemporaryDirectoryHandle();
        var readyPath = Path.Combine(directory.Path, ReadyMarkerFileName);

        var start = ScaleLadderChildProcess.Create(
            "scaleladder-crash-child",
            directory.Path,
            recordCount.ToString(CultureInfo.InvariantCulture),
            readyPath);

        using var child = Process.Start(start) ??
                          throw new InvalidOperationException("Could not start the crash-check child.");
        var standardError = child.StandardError.ReadToEndAsync();

        using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            while (!File.Exists(readyPath))
            {
                if (child.HasExited)
                {
                    return (false, $"Crash-check child exited early with code {child.ExitCode}; " +
                                   $"stderr={await standardError}");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), readyTimeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(child);
            await child.WaitForExitAsync(CancellationToken.None);
            return (false, "Crash-check child did not become ready within 60 seconds.");
        }

        // The abrupt crash boundary: no graceful shutdown, no flush of anything still buffered
        // beyond what Sync durability already forced to the WAL.
        TryKillProcessTree(child);
        await child.WaitForExitAsync(CancellationToken.None);

        // Killing the process doesn't release its still-held write lease immediately — the
        // lease's staleness is judged by its recorded acquisition time plus a clock-skew
        // tolerance, not by OS file-handle release, so a fresh open right after the kill would
        // still see it as held. Backdate it, matching the existing crash-recovery test
        // convention (PantsCommitCoalescingCrashRecoveryTests.ExpireCrashedProcessLeaseAsync).
        await ExpireCrashedProcessLeaseAsync(directory.Path);

        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < recordCount; index++)
        {
            var value = await reader.GetAsync(KeyFor(index));
            if (value is null || !value.Value.Span.SequenceEqual(ValueFor(index)))
            {
                return (false, $"Record {index} did not recover correctly after the crash.");
            }
        }

        return (true, $"{recordCount:N0} durably-committed records recovered correctly " +
                      "after an abrupt process kill.");
    }

    public static async Task RunChildAsync(string databasePath, int recordCount, string readyMarkerPath)
    {
        var options = PantsOpenOptions.Local(databasePath).WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenAsync(options);
        for (var index = 0; index < recordCount; index++)
        {
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(KeyFor(index), ValueFor(index));
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        await File.WriteAllTextAsync(readyMarkerPath, "ready");

        // Wait to be killed by the parent — an abrupt process termination, not a graceful exit.
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    static byte[] KeyFor(int index) => Encoding.UTF8.GetBytes($"crash-{index:D8}");

    static byte[] ValueFor(int index)
    {
        var value = new byte[64];
        Encoding.UTF8.GetBytes($"value-{index:D8}").CopyTo(value, 0);
        return value;
    }

    static async Task ExpireCrashedProcessLeaseAsync(string databasePath)
    {
        var leasePath = Path.Combine(databasePath, ".midge_leader");
        if (!File.Exists(leasePath))
        {
            return;
        }

        var lines = await File.ReadAllLinesAsync(leasePath);
        for (var index = 0; index < lines.Length; index++)
        {
            if (lines[index].StartsWith("acquired_at: ", StringComparison.Ordinal))
            {
                lines[index] = "acquired_at: 1970-01-01T00:00:00Z";
            }
        }

        await File.WriteAllLinesAsync(leasePath, lines);
        var acquisitionLockPath = Path.Combine(databasePath, ".midge_leader.lock");
        if (File.Exists(acquisitionLockPath))
        {
            File.Delete(acquisitionLockPath);
        }
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

    sealed class TemporaryDirectoryHandle : IDisposable
    {
        public TemporaryDirectoryHandle()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"pants-scale-ladder-crash-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
