using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Xunit.Sdk;

namespace Cntryl.Pants.Tests.Transactions.Spill;

[Collection(CrashProcessTestGroup.Name)]
public sealed class PantsTransactionSpillCrashRecoveryTests
{
    const byte TransactionBeginOperation = 4;
    const byte TransactionCommitOperation = 5;
    const byte TransactionBatchOperation = 6;
    const string ChildScenarioEnvironmentVariable = "PANTS_SPILL_CRASH_SCENARIO";
    const string DatabasePathEnvironmentVariable = "PANTS_SPILL_CRASH_DATABASE_PATH";
    const string TriggerSentinelEnvironmentVariable = "PANTS_CRASH_TRIGGER_SENTINEL";
    const string BeforeCommitMarkerScenario = "before-commit-marker";
    const string SpilledCommitTrigger = "midge::wal::spilled_txn_after_ops_append_before_commit";
    const string ReadyFileName = "spill-child-ready";

    [Fact]
    public async Task ShouldAbortInChildProcessWhenSpilledTransactionCommitIsInterrupted()
    {
        var scenario = Environment.GetEnvironmentVariable(ChildScenarioEnvironmentVariable);
        if (!StringComparer.Ordinal.Equals(scenario, BeforeCommitMarkerScenario))
        {
            return;
        }

        var databasePath = Assert.IsType<string>(
            Environment.GetEnvironmentVariable(DatabasePathEnvironmentVariable));
        var spilledBoundary = TransactionSpillHardeningTestHarness.GetRequiredFailpoint(
            "BeforeSpilledTransactionCommitMarker");
        var sentinelPath = Assert.IsType<string>(
            Environment.GetEnvironmentVariable(TriggerSentinelEnvironmentVariable));
        var handler = new TransactionSpillCrashFailpointHandler(
            spilledBoundary,
            sentinelPath,
            BeforeCommitMarkerScenario,
            SpilledCommitTrigger);
        await using var database = await TransactionSpillHardeningTestHarness.OpenLocalForTestingAsync(
            databasePath,
            handler);
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        TransactionSpillHardeningTestHarness.Fill(transaction, "crash", 12);
        var spillCount = TransactionSpillHardeningTestHarness.FindArtifacts(databasePath).Length;
        await PublishSignalAsync(
            Path.Combine(databasePath, ReadyFileName),
            spillCount.ToString(CultureInfo.InvariantCulture));

        await transaction.CommitAsync(PantsWriteOptions.Sync);

        throw new XunitException(
            "The child commit returned without aborting before its commit marker.");
    }

    [Fact]
    public async Task ShouldHideSpilledTransactionWhenCommitMarkerIsMissing()
    {
        _ = TransactionSpillHardeningTestHarness.GetRequiredFailpoint(
            "BeforeSpilledTransactionCommitMarker");
        using var directory = new TemporaryDirectory();
        var sentinelPath = GetTriggerSentinelPath(directory.Path);
        if (File.Exists(sentinelPath))
        {
            File.Delete(sentinelPath);
        }

        var childRun = StartCrashChild(directory.Path, sentinelPath);
        using var child = childRun.Child;
        try
        {
            await WaitForChildReadinessAsync(child, directory.Path);
        }
        finally
        {
            await TerminateCrashChildAsync(child, directory.Path);
        }

        var standardOutput = await childRun.StandardOutput;
        var standardError = await childRun.StandardError;
        ValidateChildCrash(child, sentinelPath, standardOutput, standardError);
        await ExpireCrashedProcessLeaseAsync(directory.Path);
        var spillCountText = await File.ReadAllTextAsync(Path.Combine(directory.Path, ReadyFileName));
        Assert.True(
            int.TryParse(
                spillCountText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var spillCount) &&
            spillCount > 0,
            "The child must have spilled before commit began.");
        var operations = TransactionSpillHardeningTestHarness.ReadWalFrames(directory.Path)
            .Select(static frame => frame.Operation)
            .ToArray();

        Assert.Contains(TransactionBeginOperation, operations);
        Assert.Contains(
            operations,
            static operation => operation is not (
                TransactionBeginOperation or TransactionCommitOperation or TransactionBatchOperation));
        Assert.DoesNotContain(TransactionCommitOperation, operations);
        Assert.DoesNotContain(TransactionBatchOperation, operations);

        await using var reopened = await TransactionSpillHardeningTestHarness.OpenLocalAsync(directory.Path);

        Assert.Null(await TransactionSpillHardeningTestHarness.ReadTextAsync(reopened, "crash-000"));
        Assert.Empty(TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path));
    }

    static (Process Child, Task<string> StandardOutput, Task<string> StandardError) StartCrashChild(
        string databasePath,
        string sentinelPath)
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
        start.ArgumentList.Add(typeof(PantsTransactionSpillCrashRecoveryTests).Assembly.Location);
        start.ArgumentList.Add($"/Platform:{RuntimeInformation.ProcessArchitecture}");
        start.ArgumentList.Add(
            $"--Tests:{typeof(PantsTransactionSpillCrashRecoveryTests).FullName}." +
            nameof(ShouldAbortInChildProcessWhenSpilledTransactionCommitIsInterrupted));
        start.Environment[ChildScenarioEnvironmentVariable] = BeforeCommitMarkerScenario;
        start.Environment[DatabasePathEnvironmentVariable] = databasePath;
        start.Environment[TriggerSentinelEnvironmentVariable] = sentinelPath;
        var child = Process.Start(start) ??
                    throw new InvalidOperationException("Could not start the transaction spill crash child.");
        return (
            child,
            child.StandardOutput.ReadToEndAsync(),
            child.StandardError.ReadToEndAsync());
    }

    static string GetTriggerSentinelPath(string databasePath) =>
        Path.Combine(
            databasePath,
            $".pants-crash-trigger-{BeforeCommitMarkerScenario}.sentinel");

    static void ValidateChildCrash(
        Process child,
        string sentinelPath,
        string standardOutput,
        string standardError)
    {
        var output = $"{standardOutput}\n{standardError}";
        Assert.NotEqual(0, child.ExitCode);
        Assert.True(
            output.Contains("test run aborted", StringComparison.OrdinalIgnoreCase) ||
            (output.Contains("testhost", StringComparison.OrdinalIgnoreCase) &&
             output.Contains("crash", StringComparison.OrdinalIgnoreCase)),
            $"The child failed without an observed testhost abort; " +
            $"exit={child.ExitCode}; stdout={standardOutput}; stderr={standardError}");
        var expected = $"scenario={BeforeCommitMarkerScenario}\ntrigger={SpilledCommitTrigger}\n";
        var reached = File.Exists(sentinelPath)
            ? File.ReadAllText(sentinelPath)
            : null;
        Assert.Equal(expected, reached);
    }

    static async Task WaitForChildReadinessAsync(Process child, string databasePath)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var readyPath = Path.Combine(databasePath, ReadyFileName);
        try
        {
            while (!File.Exists(readyPath))
            {
                if (child.HasExited)
                {
                    throw new XunitException(
                        $"Transaction spill crash child exited with code {child.ExitCode} before readiness.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            }
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new XunitException(
                "Transaction spill crash child did not become ready within 30 seconds.",
                exception);
        }
    }

    static async Task TerminateCrashChildAsync(Process child, string databasePath)
    {
        try
        {
            await WaitForCrashChildExitAsync(child);
        }
        finally
        {
            try
            {
                TryKillProcessTree(child);
                await WaitForCrashChildCleanupExitAsync(child);
            }
            finally
            {
                await WaitForCrashChildLockReleaseAsync(databasePath);
            }
        }
    }

    static async Task WaitForCrashChildExitAsync(Process child)
    {
        if (child.HasExited)
        {
            return;
        }

        using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await child.WaitForExitAsync(exitTimeout.Token);
        }
        catch (OperationCanceledException exception) when (exitTimeout.IsCancellationRequested)
        {
            throw new XunitException(
                "Transaction spill crash child did not exit within 10 seconds after readiness.",
                exception);
        }
    }

    static async Task WaitForCrashChildCleanupExitAsync(Process child)
    {
        if (child.HasExited)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await child.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new XunitException(
                "Transaction spill crash process tree did not exit within the cleanup deadline.",
                exception);
        }
    }

    static async Task WaitForCrashChildLockReleaseAsync(string databasePath)
    {
        var lockPath = Path.Combine(databasePath, "LOCK");
        if (!File.Exists(lockPath))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (true)
            {
                try
                {
                    using var stream = new FileStream(
                        lockPath,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.None);
                    return;
                }
                catch (IOException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
                }
            }
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new XunitException(
                "Transaction spill crash child did not release the writer lock within 30 seconds.",
                exception);
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
            // The process exited after HasExited was observed.
        }
        catch (Win32Exception)
        {
            // The bounded timeout remains the primary failure.
        }
        catch (NotSupportedException)
        {
            // Lock-release validation below still detects a surviving testhost.
        }
    }

    static async Task PublishSignalAsync(string path, string contents)
    {
        var temporaryPath = $"{path}.{Environment.ProcessId}.tmp";
        await File.WriteAllTextAsync(temporaryPath, contents);
        File.Move(temporaryPath, path);
    }

    static async Task ExpireCrashedProcessLeaseAsync(string databasePath)
    {
        var leasePath = Path.Combine(databasePath, ".midge_leader");
        var lines = await File.ReadAllLinesAsync(leasePath);
        var updated = false;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].StartsWith("acquired_at: ", StringComparison.Ordinal))
            {
                continue;
            }

            lines[index] = "acquired_at: 1970-01-01T00:00:00Z";
            updated = true;
        }

        Assert.True(updated, "The crashed process lease must contain an acquisition timestamp.");
        await File.WriteAllLinesAsync(leasePath, lines);
        var acquisitionLockPath = Path.Combine(databasePath, ".midge_leader.lock");
        if (File.Exists(acquisitionLockPath))
        {
            File.Delete(acquisitionLockPath);
        }
    }
}
