using System.Diagnostics;
using System.Text.Json;
using Cntryl.Pants.Exceptions;

namespace Cntryl.Pants.Destroyer.Support;

/// <summary>
/// Shared support for scenarios that need a real, separate OS process to
/// crash: spawns this same assembly's own apphost as a worker (see
/// <c>Program.cs</c>) that opens a local Pants database and commits a
/// deterministic <c>Put</c> stream, reporting one JSON line per acked
/// operation. A real subprocess kill is the only way to reproduce true
/// crash behavior (partial writes, fsync ordering across an actual process
/// death) rather than simulating one in-process.
/// </summary>
public static class DestroyerWorker
{
    /// <summary>The worker executable: this same assembly's own apphost.</summary>
    public static string ExecutablePath { get; } = Path.ChangeExtension(
        typeof(DestroyerWorker).Assembly.Location,
        OperatingSystem.IsWindows() ? "exe" : null);

    /// <summary>
    /// Starts the worker against <paramref name="dbPath"/>, reads acked
    /// operations off its stdout until at least <paramref name="ackThreshold"/>
    /// have been reported, then hard-kills the process (crash injection) and
    /// returns every (sequence, key) the worker reported as acked.
    /// </summary>
    public static async Task<List<(int Sequence, string Key)>> RunUntilAckedThenKillAsync(
        string dbPath,
        int operationCount,
        ulong seed,
        int ackThreshold)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(ExecutablePath, $"\"{dbPath}\" {operationCount} {seed}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        var ackedKeys = new List<(int Sequence, string Key)>();

        process.Start();
        try
        {
            while (ackedKeys.Count < ackThreshold)
            {
                var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
                if (line is null)
                {
                    var stderr = await process.StandardError.ReadToEndAsync();
                    throw new InvalidOperationException(
                        $"worker exited after {ackedKeys.Count} acked operations, before reaching the crash threshold of {ackThreshold}. stderr: {stderr}");
                }

                var report = JsonSerializer.Deserialize<JsonElement>(line);
                if (report.GetProperty("status").GetString() == "acked")
                {
                    ackedKeys.Add((
                        report.GetProperty("sequence").GetInt32(),
                        report.GetProperty("key").GetString()!));
                }
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
        }

        return ackedKeys;
    }

    /// <summary>
    /// Retries opening a database left behind by a killed worker until the
    /// writer lease's takeover window elapses (Pants requires the lease to
    /// age past a fixed base delay plus clock-skew tolerance before another
    /// writer may claim it — see <c>FileLease.Acquire</c>). Every rejection
    /// along the way must be <see cref="PantsLeaseHeldException"/>,
    /// confirming the database is unavailable rather than silently
    /// corrupted while the stale lease is honored.
    /// </summary>
    public static async Task<IPantsDatabase> ReopenAfterLeaseTakeoverAsync(
        string dbPath,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            try
            {
                return await PantsDatabase.OpenAsync(PantsOpenOptions.Local(dbPath));
            }
            catch (PantsLeaseHeldException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }
}
