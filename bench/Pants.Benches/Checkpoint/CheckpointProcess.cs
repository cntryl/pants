using System.Diagnostics;
using System.Text;

namespace Cntryl.Pants.Checkpoint;

static class CheckpointProcess
{
    public static async Task<CheckpointEvidence.ProcessOutcome> RunAsync(ProcessStartInfo start, string selector,
        TimeSpan allowance, Func<ulong?>? progress = null, TimeSpan? noProgressAllowance = null, TimeProvider? timeProvider = null)
    {
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        var time = timeProvider ?? TimeProvider.System;
        var started = time.GetTimestamp();
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            return new CheckpointEvidence.ProcessOutcome(0, selector, -1, false, "", "", exception.ToString(),
                CheckpointClock.Nanoseconds(time.GetElapsedTime(started)));
        }
        var processId = process.Id;
        var output = CaptureAsync(process.StandardOutput);
        var error = CaptureAsync(process.StandardError);
        var killed = false;
        using var deadline = new CancellationTokenSource(allowance);
        using var monitorCancellation = new CancellationTokenSource();
        string? terminationReason = null;
        var monitor = progress is null ? Task.CompletedTask : MonitorAsync();
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            terminationReason ??= "original process allowance expired";
            killed = true;
            KillIfRunning(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            terminationReason = $"Actual process observation failed: {exception}";
            killed = true;
            KillIfRunning(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        finally
        {
            monitorCancellation.Cancel();
            await monitor.ConfigureAwait(false);
        }
        return new CheckpointEvidence.ProcessOutcome(processId, selector, process.ExitCode, killed,
            await output.ConfigureAwait(false), await error.ConfigureAwait(false), null,
            CheckpointClock.Nanoseconds(time.GetElapsedTime(started)))
        {
            TerminationReason = terminationReason
        };

        async Task MonitorAsync()
        {
            var lastProgress = 0UL;
            var lastChange = time.GetTimestamp();
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), monitorCancellation.Token).ConfigureAwait(false);
                    var observed = progress!();
                    if (observed is { } count && count > lastProgress)
                    {
                        lastChange = time.GetTimestamp();
                        lastProgress = count;
                    }
                    else if (observed is { } previous && previous < lastProgress || time.GetElapsedTime(lastChange) >= (noProgressAllowance ?? TimeSpan.FromSeconds(60)))
                    {
                        terminationReason = observed is { } decreased && decreased < lastProgress ? "genuine progress decreased" : "genuine progress allowance expired";
                        deadline.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested)
            {
                // Process completion stops the independent watchdog; it adds no benchmark work.
            }
            catch (Exception exception)
            {
                terminationReason = $"Actual progress observation failed: {exception}";
                deadline.Cancel();
            }
        }
    }

    static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // Exit between observation and kill is already a stopped owner.
        }
    }

    static async Task<string> CaptureAsync(StreamReader reader)
    {
        const int limit = 64 * 1024;
        var text = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
            {
                return text.ToString();
            }
            var retained = Math.Min(read, limit - text.Length);
            if (retained > 0)
            {
                text.Append(buffer, 0, retained);
            }
        }
    }
}
