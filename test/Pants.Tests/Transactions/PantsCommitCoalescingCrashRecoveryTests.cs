using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;
using Xunit.Sdk;

namespace Cntryl.Pants.Transactions;

[Collection(CrashProcessTestGroup.Name)]
public sealed class PantsCommitCoalescingCrashRecoveryTests
{
    const int CommitCount = 32;
    const string ChildScenarioEnvironmentVariable = "PANTS_COALESCED_COMMIT_CRASH_SCENARIO";
    const string DatabasePathEnvironmentVariable = "PANTS_COALESCED_COMMIT_CRASH_DATABASE_PATH";
    const string AfterSharedSyncScenario = "after-shared-sync";
    const string RollbackFailureScenario = "rollback-failure";
    const string SingleRollbackFailureScenario = "single-rollback-failure";
    const string ReadyFileName = "coalesced-commit-child.ready";
    const string SentinelFileName = "coalesced-commit-child.crashed";
    const string RollbackFailureSentinelFileName = "coalesced-rollback-failure-child.crashed";
    const string SingleRollbackFailureSentinelFileName = "wal-rollback-failure-child.crashed";

    [Fact]
    public async Task ShouldAbortInChildProcessAfterCoalescedWalDurabilityBoundary()
    {
        var scenario = Environment.GetEnvironmentVariable(ChildScenarioEnvironmentVariable);
        if (!StringComparer.Ordinal.Equals(scenario, AfterSharedSyncScenario))
        {
            return;
        }

        var databasePath = Assert.IsType<string>(
            Environment.GetEnvironmentVariable(DatabasePathEnvironmentVariable));
        using var handler = new CoalescedCommitCrashFailpointHandler(
            Path.Combine(databasePath, SentinelFileName),
            CommitCount);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(databasePath)
                .WithBackgroundCompaction(false),
            new RuntimeDependencies(handler));
        var transactions = new IPantsTransaction[CommitCount];
        for (var index = 0; index < transactions.Length; index++)
        {
            var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(GetKey(index), GetValue(index));
            transactions[index] = transaction;
        }

        var blockedMetrics = database.Diagnostics.GetRuntimeMetricsAsync().AsTask();
        await handler.WaitForRuntimeBarrierAsync(TimeSpan.FromSeconds(10));

        var commits = new Task[transactions.Length];
        for (var index = 0; index < transactions.Length; index++)
        {
            commits[index] = transactions[index]
                .CommitAsync(PantsWriteOptions.Sync)
                .AsTask();
        }

        await PublishSignalAsync(
            Path.Combine(databasePath, ReadyFileName),
            CommitCount.ToString(CultureInfo.InvariantCulture));
        handler.ReleaseRuntimeBarrier();

        await Task.WhenAll(commits).WaitAsync(TimeSpan.FromSeconds(30));
        _ = await blockedMetrics;
        throw new XunitException(
            "The child completed every commit without aborting after the shared WAL sync.");
    }

    [Fact]
    public async Task ShouldRecoverEveryAcceptedCommitWhenProcessAbortsAfterCoalescedWalSync()
    {
        using var directory = new TemporaryDirectory();
        var childRun = StartCrashChild(directory.Path);
        using var child = childRun.Child;
        try
        {
            await WaitForChildReadinessAsync(child, directory.Path);
            await WaitForCrashChildExitAsync(child);
        }
        finally
        {
            TryKillProcessTree(child);
            await WaitForCrashChildLockReleaseAsync(directory.Path);
        }

        var standardOutput = await childRun.StandardOutput;
        var standardError = await childRun.StandardError;
        ValidateChildCrash(child, directory.Path, standardOutput, standardError);
        await ExpireCrashedProcessLeaseAsync(directory.Path);

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithBackgroundCompaction(false));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < CommitCount; index++)
        {
            var value = await reader.GetAsync(GetKey(index));
            Assert.Equal(
                TestBytes.ToText(GetValue(index)),
                TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(value)));
        }
    }

    [Fact]
    public async Task ShouldFenceWalSuffixAndLayoutInChildProcessWhenGroupRollbackFails()
    {
        var scenario = Environment.GetEnvironmentVariable(ChildScenarioEnvironmentVariable);
        if (!StringComparer.Ordinal.Equals(scenario, RollbackFailureScenario))
        {
            return;
        }

        var databasePath = Assert.IsType<string>(
            Environment.GetEnvironmentVariable(DatabasePathEnvironmentVariable));
        using var handler = new CoalescedCommitRollbackFailureFailpointHandler();
        var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(databasePath)
                .WithBackgroundCompaction(false),
            new RuntimeDependencies(handler));
        await using (var pendingFlush = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            pendingFlush.Put("pending-flush"u8.ToArray(), "value"u8.ToArray());
            await pendingFlush.CommitAsync(PantsWriteOptions.BestEffort);
        }

        var transactions = new IPantsTransaction[3];
        for (var index = 0; index < transactions.Length; index++)
        {
            var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(
                TestBytes.FromString($"uncertain-key-{index}"),
                TestBytes.FromString($"uncertain-value-{index}"));
            transactions[index] = transaction;
        }

        var blockedMetrics = database.Diagnostics.GetRuntimeMetricsAsync().AsTask();
        await handler.WaitForRuntimeBarrierAsync(TimeSpan.FromSeconds(10));
        var commits = transactions
            .Select(transaction => transaction.CommitAsync(PantsWriteOptions.Sync).AsTask())
            .ToArray();
        await PublishSignalAsync(
            Path.Combine(databasePath, ReadyFileName),
            transactions.Length.ToString(CultureInfo.InvariantCulture));
        handler.ReleaseRuntimeBarrier();
        _ = await blockedMetrics;

        foreach (var commit in commits)
        {
            await Assert.ThrowsAsync<PantsAbortedException>(() => commit.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        await using (var suffix = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            suffix.Put("fenced-suffix"u8.ToArray(), "must-not-recover"u8.ToArray());
            await Assert.ThrowsAsync<PantsAbortedException>(() => suffix.CommitAsync(PantsWriteOptions.Sync).AsTask());
        }

        await Assert.ThrowsAsync<PantsAbortedException>(() =>
            database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask());
        await Assert.ThrowsAsync<PantsAbortedException>(() => database.Maintenance.CompactAllAsync().AsTask());
        await Assert.ThrowsAsync<PantsAbortedException>(() =>
            database.ColumnFamilies.CreateAsync("fenced-family").AsTask());
        await Assert.ThrowsAsync<PantsAbortedException>(() => database.ShutdownAsync(TimeSpan.FromSeconds(5)).AsTask());

        WriteDurableSignal(
            Path.Combine(databasePath, RollbackFailureSentinelFileName),
            "rollback-failure-fenced\n");
        Environment.FailFast("Injected crash after validating the uncertain-WAL fence.");
    }

    [Fact]
    public async Task ShouldFenceWalSuffixInChildProcessWhenSingleRollbackFails()
    {
        var scenario = Environment.GetEnvironmentVariable(ChildScenarioEnvironmentVariable);
        if (!StringComparer.Ordinal.Equals(scenario, SingleRollbackFailureScenario))
        {
            return;
        }

        var databasePath = Assert.IsType<string>(
            Environment.GetEnvironmentVariable(DatabasePathEnvironmentVariable));
        var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(databasePath)
                .WithBackgroundCompaction(false),
            new RuntimeDependencies(new WalRollbackFailureFailpointHandler()));
        await using (var rejected = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            rejected.Put("uncertain-single"u8.ToArray(), "uncertain"u8.ToArray());
            await Assert.ThrowsAsync<PantsAbortedException>(() =>
                rejected.CommitAsync(PantsWriteOptions.Sync).AsTask());
        }

        await using (var suffix = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            suffix.Put("fenced-single-suffix"u8.ToArray(), "must-not-recover"u8.ToArray());
            await Assert.ThrowsAsync<PantsAbortedException>(() => suffix.CommitAsync(PantsWriteOptions.Sync).AsTask());
        }

        await Assert.ThrowsAsync<PantsAbortedException>(() =>
            database.ColumnFamilies.CreateAsync("fenced-single-family").AsTask());
        await Assert.ThrowsAsync<PantsAbortedException>(() => database.ShutdownAsync(TimeSpan.FromSeconds(5)).AsTask());

        WriteDurableSignal(
            Path.Combine(databasePath, SingleRollbackFailureSentinelFileName),
            "single-rollback-failure-fenced\n");
        Environment.FailFast("Injected crash after validating the single-WAL fence.");
    }

    [Fact]
    public async Task ShouldNotRecoverFencedSuffixGivenCoalescedRollbackUncertainty()
    {
        using var directory = new TemporaryDirectory();
        var childRun = StartRollbackFailureChild(directory.Path);
        using var child = childRun.Child;
        try
        {
            await WaitForChildReadinessAsync(child, directory.Path);
            await WaitForCrashChildExitAsync(child);
        }
        finally
        {
            TryKillProcessTree(child);
            await WaitForCrashChildLockReleaseAsync(directory.Path);
        }

        var standardOutput = await childRun.StandardOutput;
        var standardError = await childRun.StandardError;
        Assert.NotEqual(0, child.ExitCode);
        var sentinelPath = Path.Combine(directory.Path, RollbackFailureSentinelFileName);
        Assert.True(
            File.Exists(sentinelPath) &&
            StringComparer.Ordinal.Equals(
                "rollback-failure-fenced\n",
                await File.ReadAllTextAsync(sentinelPath)),
            $"The child did not validate the uncertain-WAL fence; " +
            $"exit={child.ExitCode}; stdout={standardOutput}; stderr={standardError}");
        await ExpireCrashedProcessLeaseAsync(directory.Path);

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithBackgroundCompaction(false));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync("fenced-suffix"u8.ToArray()));
    }

    [Fact]
    public async Task ShouldNotRecoverFencedSuffixGivenSingleRollbackUncertainty()
    {
        using var directory = new TemporaryDirectory();
        var childRun = StartSingleRollbackFailureChild(directory.Path);
        using var child = childRun.Child;
        try
        {
            await WaitForCrashChildExitAsync(child);
        }
        finally
        {
            TryKillProcessTree(child);
            await WaitForCrashChildLockReleaseAsync(directory.Path);
        }

        var standardOutput = await childRun.StandardOutput;
        var standardError = await childRun.StandardError;
        Assert.NotEqual(0, child.ExitCode);
        var sentinelPath = Path.Combine(directory.Path, SingleRollbackFailureSentinelFileName);
        Assert.True(
            File.Exists(sentinelPath) &&
            StringComparer.Ordinal.Equals(
                "single-rollback-failure-fenced\n",
                await File.ReadAllTextAsync(sentinelPath)),
            $"The child did not validate the single-WAL fence; " +
            $"exit={child.ExitCode}; stdout={standardOutput}; stderr={standardError}");
        await ExpireCrashedProcessLeaseAsync(directory.Path);

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithBackgroundCompaction(false));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync("fenced-single-suffix"u8.ToArray()));
    }

    static (Process Child, Task<string> StandardOutput, Task<string> StandardError) StartCrashChild(
        string databasePath) =>
        StartCrashChild(
            databasePath,
            nameof(ShouldAbortInChildProcessAfterCoalescedWalDurabilityBoundary),
            AfterSharedSyncScenario,
            "coalesced-commit crash child");

    static (Process Child, Task<string> StandardOutput, Task<string> StandardError)
        StartRollbackFailureChild(string databasePath) =>
        StartCrashChild(
            databasePath,
            nameof(ShouldFenceWalSuffixAndLayoutInChildProcessWhenGroupRollbackFails),
            RollbackFailureScenario,
            "coalesced-rollback-failure child");

    static (Process Child, Task<string> StandardOutput, Task<string> StandardError)
        StartSingleRollbackFailureChild(string databasePath) =>
        StartCrashChild(
            databasePath,
            nameof(ShouldFenceWalSuffixInChildProcessWhenSingleRollbackFails),
            SingleRollbackFailureScenario,
            "single-rollback-failure child");

    static (Process Child, Task<string> StandardOutput, Task<string> StandardError) StartCrashChild(
        string databasePath,
        string childTestName,
        string scenario,
        string childDescription)
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
        start.ArgumentList.Add(typeof(PantsCommitCoalescingCrashRecoveryTests).Assembly.Location);
        start.ArgumentList.Add($"/Platform:{RuntimeInformation.ProcessArchitecture}");
        start.ArgumentList.Add(
            $"--Tests:{typeof(PantsCommitCoalescingCrashRecoveryTests).FullName}." +
            childTestName);
        start.Environment[ChildScenarioEnvironmentVariable] = scenario;
        start.Environment[DatabasePathEnvironmentVariable] = databasePath;
        var child = Process.Start(start) ??
                    throw new InvalidOperationException($"Could not start the {childDescription}.");
        return (
            child,
            child.StandardOutput.ReadToEndAsync(),
            child.StandardError.ReadToEndAsync());
    }

    static async Task WaitForChildReadinessAsync(Process child, string databasePath)
    {
        var readyPath = Path.Combine(databasePath, ReadyFileName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (!File.Exists(readyPath))
            {
                if (child.HasExited)
                {
                    throw new XunitException(
                        $"Coalesced-commit crash child exited with code {child.ExitCode} " +
                        "before readiness.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            }
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new XunitException(
                "Coalesced-commit crash child did not become ready within 30 seconds.",
                exception);
        }
    }

    static async Task WaitForCrashChildExitAsync(Process child)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await child.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new XunitException(
                "Coalesced-commit crash child did not exit within 15 seconds after readiness.",
                exception);
        }
    }

    static void ValidateChildCrash(
        Process child,
        string databasePath,
        string standardOutput,
        string standardError)
    {
        Assert.NotEqual(0, child.ExitCode);
        var expected =
            $"trigger={Failpoint.AfterCoalescedWalDurabilityBoundary}\n" +
            $"expected-commits={CommitCount}\n";
        var sentinelPath = Path.Combine(databasePath, SentinelFileName);
        var actual = File.Exists(sentinelPath)
            ? File.ReadAllText(sentinelPath)
            : null;
        Assert.True(
            StringComparer.Ordinal.Equals(expected, actual),
            $"The child did not reach the post-sync crash boundary; " +
            $"exit={child.ExitCode}; sentinel={actual ?? "<missing>"}; " +
            $"stdout={standardOutput}; stderr={standardError}");
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
                "Coalesced-commit crash child did not release the writer lock within 30 seconds.",
                exception);
        }
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

    static async Task PublishSignalAsync(string path, string contents)
    {
        var temporaryPath = $"{path}.{Environment.ProcessId}.tmp";
        await File.WriteAllTextAsync(temporaryPath, contents);
        File.Move(temporaryPath, path);
    }

    static void WriteDurableSignal(string path, string contents)
    {
        var bytes = Encoding.UTF8.GetBytes(contents);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            4_096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(true);
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
            // Lock-release validation below remains the primary failure.
        }
        catch (NotSupportedException)
        {
            // Lock-release validation below remains the primary failure.
        }
    }

    static byte[] GetKey(int index) => TestBytes.FromString($"coalesced-key-{index:D2}");

    static byte[] GetValue(int index) => TestBytes.FromString($"coalesced-value-{index:D2}");
}
