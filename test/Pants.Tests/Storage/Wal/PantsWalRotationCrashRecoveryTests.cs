using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Cntryl.Pants.Support.TestDoubles;
using Xunit.Sdk;

namespace Cntryl.Pants.Storage.Wal;

[Collection(CrashProcessTestGroup.Name)]
public sealed class PantsWalRotationCrashRecoveryTests
{
    const string BoundaryEnvironmentVariable = "PANTS_WAL_ROTATION_CRASH_BOUNDARY";
    const string DatabaseEnvironmentVariable = "PANTS_WAL_ROTATION_CRASH_DATABASE";
    const string SentinelFileName = "wal-rotation-child.crashed";

    static readonly ColumnFamilyIdentity DefaultFamily = new(
        0,
        "default",
        RuntimeState.DefaultFamilyVersion);

    [Fact]
    public void ShouldAbortAtWalRotationBoundaryInChildProcess()
    {
        var boundaryName = Environment.GetEnvironmentVariable(BoundaryEnvironmentVariable);
        if (boundaryName is null)
        {
            return;
        }

        var path = Assert.IsType<string>(
            Environment.GetEnvironmentVariable(DatabaseEnvironmentVariable));
        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            new RuntimeTelemetry());
        var failpoints = new RotationCrashFailpointHandler(
            Enum.Parse<Failpoint>(boundaryName),
            Path.Combine(path, SentinelFileName));
        using var store = LocalDiskStore.Open(path, state, failpoints: failpoints);
        _ = store.AppendCommit(
            CreateCommitPayload(state, "accepted", "durable"),
            state,
            PantsDurability.Sync);

        failpoints.RejectNextAppend();
        _ = Assert.Throws<IOException>(() => store.AppendCommit(
            CreateCommitPayload(state, "rejected", "ghost"),
            state,
            PantsDurability.Sync));
        failpoints.ArmCrash();
        _ = store.RotateActiveLocalWal();
        throw new XunitException("The child passed the WAL rotation crash boundary.");
    }

    [Theory]
    [InlineData(nameof(Failpoint.BeforeWalReplacementWriterCreate))]
    [InlineData(nameof(Failpoint.AfterWalReplacementWriterCreate))]
    public async Task ShouldRecoverOnlyAcceptedWriteAfterRotationProcessAbort(string boundaryName)
    {
        using var directory = new TemporaryDirectory();
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
        start.ArgumentList.Add(typeof(PantsWalRotationCrashRecoveryTests).Assembly.Location);
        start.ArgumentList.Add($"/Platform:{RuntimeInformation.ProcessArchitecture}");
        start.ArgumentList.Add(
            $"--Tests:{typeof(PantsWalRotationCrashRecoveryTests).FullName}." +
            nameof(ShouldAbortAtWalRotationBoundaryInChildProcess));
        start.Environment[BoundaryEnvironmentVariable] = boundaryName;
        start.Environment[DatabaseEnvironmentVariable] = directory.Path;
        using var child = Process.Start(start) ??
                          throw new InvalidOperationException("Could not start the WAL rotation child.");
        var standardOutput = child.StandardOutput.ReadToEndAsync();
        var standardError = child.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await child.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            TryKillProcessTree(child);
        }

        var output = await standardOutput;
        var error = await standardError;
        var sentinelPath = Path.Combine(directory.Path, SentinelFileName);
        var sentinel = File.Exists(sentinelPath)
            ? await File.ReadAllTextAsync(sentinelPath)
            : null;
        Assert.True(
            child.ExitCode != 0 && StringComparer.Ordinal.Equals(boundaryName, sentinel),
            $"Rotation child did not abort at {boundaryName}: exit={child.ExitCode}; " +
            $"sentinel={sentinel ?? "<missing>"}; stdout={output}; stderr={error}");

        await WaitForLockReleaseAsync(directory.Path);
        await ExpireCrashedLeaseAsync(directory.Path);
        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "durable",
            TestBytes.ToText((await reader.GetAsync("accepted"u8.ToArray()))!.Value));
        Assert.Null(await reader.GetAsync("rejected"u8.ToArray()));
    }

    static CommitPayload CreateCommitPayload(RuntimeState state, string key, string value)
    {
        var operation = new TransactionIntentOperation(
            0,
            CommitOperationKind.Put,
            DefaultFamily,
            TestBytes.FromString(key),
            null,
            TestBytes.FromString(value),
            null,
            null,
            false);
        var source = new TransactionOperationSource(
            null,
            [operation],
            1,
            DateTimeOffset.UnixEpoch);
        return new CommitPayload(
            1,
            PantsTransactionMode.ReadWrite,
            PantsConflictPolicy.LastWriteWins,
            DateTimeOffset.UnixEpoch,
            state.CreateVersion(),
            source,
            []);
    }

    static async Task WaitForLockReleaseAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                using var stream = new FileStream(
                    Path.Combine(path, "LOCK"),
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return;
            }
            catch (IOException) when (!timeout.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            }
        }
    }

    static async Task ExpireCrashedLeaseAsync(string path)
    {
        var leasePath = Path.Combine(path, ".midge_leader");
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

        Assert.True(updated);
        await File.WriteAllLinesAsync(leasePath, lines);
        var acquisitionLockPath = Path.Combine(path, ".midge_leader.lock");
        if (File.Exists(acquisitionLockPath))
        {
            File.Delete(acquisitionLockPath);
        }
    }

    static void TryKillProcessTree(Process child)
    {
        try
        {
            if (!child.HasExited)
            {
                child.Kill(true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The child exited while the test was checking it.
        }
    }

    sealed class RotationCrashFailpointHandler(Failpoint boundary, string sentinelPath) :
        IFailpointHandler
    {
        bool _crashArmed;
        bool _rejectNextAppend;

        public void RejectNextAppend() => _rejectNextAppend = true;

        public void ArmCrash() => _crashArmed = true;

        public void Hit(Failpoint failpoint)
        {
            if (failpoint == Failpoint.BeforeWalAppend && _rejectNextAppend)
            {
                _rejectNextAppend = false;
                throw new IOException("Injected pre-append rejection.");
            }

            if (failpoint != boundary || !_crashArmed)
            {
                return;
            }

            using (var sentinel = new FileStream(
                       sentinelPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read,
                       4_096,
                       FileOptions.WriteThrough))
            {
                sentinel.Write(Encoding.UTF8.GetBytes(failpoint.ToString()));
                sentinel.Flush(true);
            }

            Environment.FailFast($"Injected process abort at WAL rotation {failpoint}.");
        }
    }
}
