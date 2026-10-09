using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Xunit.Sdk;

namespace Cntryl.Pants.Support.TestDoubles;

/// <summary>
///     A crash-scenario child test run through <c>dotnet vstest</c>, with its output captured so a
///     failure can report what the child printed, how it exited, and how long it ran.
/// </summary>
sealed class CrashChildProcess : IDisposable
{
    static readonly TimeSpan DefaultReadinessTimeout = TestTimeouts.Expected;
    static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    readonly string _description;
    readonly Stopwatch _elapsed;

    CrashChildProcess(Process process, string description, Stopwatch elapsed)
    {
        Process = process;
        _description = description;
        _elapsed = elapsed;
        StandardOutput = process.StandardOutput.ReadToEndAsync();
        StandardError = process.StandardError.ReadToEndAsync();
    }

    public Process Process { get; }

    public int ExitCode => Process.ExitCode;

    public Task<string> StandardOutput { get; }

    public Task<string> StandardError { get; }

    public static ProcessStartInfo CreateStartInfo(Type testClass, string childTestName)
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
        start.ArgumentList.Add(testClass.Assembly.Location);
        start.ArgumentList.Add($"/Platform:{RuntimeInformation.ProcessArchitecture}");
        start.ArgumentList.Add($"--Tests:{testClass.FullName}.{childTestName}");
        return start;
    }

    public static CrashChildProcess Start(ProcessStartInfo start, string description)
    {
        if (!start.RedirectStandardOutput || !start.RedirectStandardError)
        {
            throw new ArgumentException("Crash children must redirect their output.", nameof(start));
        }

        var elapsed = Stopwatch.StartNew();
        var process = Process.Start(start) ??
                      throw new InvalidOperationException($"Could not start the {description}.");
        return new CrashChildProcess(process, description, elapsed);
    }

    /// <summary>
    ///     Starts the child and waits until it publishes <paramref name="readyPath" />.
    /// </summary>
    /// <remarks>
    ///     On GitHub macOS runners the <c>dotnet</c> process occasionally dies with exit 137 (SIGKILL)
    ///     within a few hundred milliseconds of starting: silently, before vstest prints its banner,
    ///     so before any test host or Pants code runs. Nothing in this suite sends that signal and
    ///     the cause is not yet known. Such a launch is retried once, and only when the child left
    ///     the database directory untouched, so a child that crashed inside the scenario still fails.
    /// </remarks>
    public static async Task<CrashChildProcess> StartAndWaitForReadinessAsync(
        ProcessStartInfo start,
        string description,
        string databasePath,
        string readyPath,
        TimeSpan? timeout = null)
    {
        string? abortedLaunch = null;
        while (true)
        {
            var child = Start(start, description);
            bool ready;
            try
            {
                ready = await child.WaitForReadinessAsync(readyPath, timeout ?? DefaultReadinessTimeout);
            }
            catch
            {
                child.Dispose();
                throw;
            }

            if (ready)
            {
                return child;
            }

            var failure = await child.DescribeAsync();
            child.Dispose();
            if (abortedLaunch is null && !HasEntries(databasePath))
            {
                abortedLaunch = failure;
                continue;
            }

            var retried = abortedLaunch is null
                ? string.Empty
                : $"; the first launch, relaunched once, had also exited without touching the database: {abortedLaunch}";
            throw new XunitException(
                $"The {description} exited before readiness: {failure}{retried}");
        }
    }

    /// <summary>
    ///     Runs the child until it exits and returns it with its output still readable.
    /// </summary>
    /// <remarks>
    ///     A launch that the macOS runner kills before vstest prints its banner (see
    ///     <see cref="StartAndWaitForReadinessAsync" />) exits non-zero with no output at all, so no
    ///     test code ran. Such a launch is retried once; a child that printed anything is returned as
    ///     is for the caller to judge.
    /// </remarks>
    public static async Task<CrashChildProcess> RunToExitAsync(
        ProcessStartInfo start,
        string description,
        TimeSpan timeout)
    {
        var relaunched = false;
        while (true)
        {
            var child = Start(start, description);
            try
            {
                await child.WaitForExitAsync(timeout);
            }
            catch
            {
                child.Dispose();
                throw;
            }

            if (!relaunched &&
                child.ExitCode != 0 &&
                (await ReadOutputAsync(child.StandardOutput)).Length == 0)
            {
                relaunched = true;
                child.Dispose();
                continue;
            }

            return child;
        }
    }

    public async Task<string> DescribeAsync()
    {
        var exit = Process.HasExited
            ? Process.ExitCode.ToString(CultureInfo.InvariantCulture)
            : "running";
        return $"exit={exit}; elapsed={_elapsed.ElapsedMilliseconds}ms; " +
               $"stdout={await ReadOutputAsync(StandardOutput)}; " +
               $"stderr={await ReadOutputAsync(StandardError)}";
    }

    public void TryKillProcessTree()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited after HasExited was observed.
        }
        catch (Win32Exception)
        {
            // The caller's bounded wait reports a child that survives.
        }
        catch (NotSupportedException)
        {
            // The caller's bounded wait reports a child that survives.
        }
    }

    public void Dispose() => Process.Dispose();

    async Task WaitForExitAsync(TimeSpan timeout)
    {
        try
        {
            await Process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException exception)
        {
            TryKillProcessTree();
            try
            {
                await Process.WaitForExitAsync().WaitAsync(OutputDrainTimeout);
            }
            catch (TimeoutException)
            {
                // The description below reports the child as still running.
            }

            throw new XunitException(
                $"The {_description} did not exit within {timeout.TotalSeconds:0} seconds: " +
                await DescribeAsync(),
                exception);
        }
    }

    async Task<bool> WaitForReadinessAsync(string readyPath, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (true)
        {
            if (File.Exists(readyPath))
            {
                return true;
            }

            if (Process.HasExited)
            {
                // The child may have published readiness just before exiting.
                return File.Exists(readyPath);
            }

            try
            {
                await Task.Delay(PollInterval, deadline.Token);
            }
            catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
            {
                TryKillProcessTree();
                try
                {
                    await Process.WaitForExitAsync().WaitAsync(OutputDrainTimeout);
                }
                catch (TimeoutException)
                {
                    // The description below reports the child as still running.
                }

                throw new XunitException(
                    $"The {_description} did not become ready within {timeout.TotalSeconds:0} seconds: " +
                    await DescribeAsync(),
                    exception);
            }
        }
    }

    static async Task<string> ReadOutputAsync(Task<string> output)
    {
        try
        {
            return (await output.WaitAsync(OutputDrainTimeout)).Trim();
        }
        catch (TimeoutException)
        {
            return "<still open>";
        }
    }

    static bool HasEntries(string path) =>
        Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
}
