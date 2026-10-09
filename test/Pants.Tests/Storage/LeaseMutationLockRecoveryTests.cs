using System.Diagnostics;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

[Collection(CrashProcessTestGroup.Name)]
public sealed class LeaseMutationLockRecoveryTests
{
    const string DatabaseEnvironmentVariable = "PANTS_LEASE_MUTATION_LOCK_CRASH_DATABASE";
    const string ReadyEnvironmentVariable = "PANTS_LEASE_MUTATION_LOCK_CRASH_READY";
    const string LockFileName = ".midge_leader.lock";

    static readonly TimeSpan LongHeartbeatInterval = TimeSpan.FromHours(1);

    [Fact]
    public void ShouldExitHoldingLeaseMutationLockInChild()
    {
        var databasePath = Environment.GetEnvironmentVariable(DatabaseEnvironmentVariable);
        if (databasePath is null)
        {
            return;
        }

        var readyPath = Assert.IsType<string>(Environment.GetEnvironmentVariable(ReadyEnvironmentVariable));
        // A fixed, long-past clock writes a record that any real-clock reader already treats as stale.
        var clock = new ManualClock(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var lease = FileLease.Acquire(
            databasePath,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval,
            clock,
            TimeSpan.FromSeconds(30));
        lease.RenewWriteInterferenceHookForTesting = () =>
        {
            // Renewal has written the record while holding the mutation lock. Stop here so the
            // parent can kill this process with the lock still on disk.
            File.WriteAllText(readyPath, "ready");
            Thread.Sleep(Timeout.Infinite);
        };

        lease.RenewForTesting();

        Assert.Fail("The child renewal finished without reaching the crash point.");
    }

    [Fact]
    public async Task ShouldRefuseOpenUntilExplicitRecoveryRemovesKilledWriterMutationLock()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = Path.Combine(directory.Path, "db");
        Directory.CreateDirectory(databasePath);
        var readyPath = Path.Combine(directory.Path, "renewal.ready");
        var lockPath = Path.Combine(databasePath, LockFileName);

        var start = CrashChildProcess.CreateStartInfo(
            typeof(LeaseMutationLockRecoveryTests),
            nameof(ShouldExitHoldingLeaseMutationLockInChild));
        start.Environment[DatabaseEnvironmentVariable] = databasePath;
        start.Environment[ReadyEnvironmentVariable] = readyPath;
        using (var child = await CrashChildProcess.StartAndWaitForReadinessAsync(
                   start,
                   "lease mutation lock crash child",
                   databasePath,
                   readyPath))
        {
            // The child still holds the lock, so recovery must refuse rather than fail with a raw
            // sharing violation.
            await Assert.ThrowsAsync<PantsLeaseHeldException>(
                () => PantsDatabase.RecoverStaleLeaseMutationLockAsync(databasePath).AsTask());
            Assert.True(File.Exists(lockPath));

            child.TryKillProcessTree();
            await child.Process.WaitForExitAsync().WaitAsync(TestTimeouts.Expected);
            Assert.NotEqual(0, child.ExitCode);
        }

        // The vstest test host that owns the lock can outlive its launcher for a moment after the
        // kill. Wait until the kernel has released its handle, as the other crash tests do.
        await WaitForLockReleaseAsync(lockPath);
        Assert.True(File.Exists(lockPath), "The killed writer must leave its mutation lock behind.");

        // The lock is sticky: a plain open refuses while it is present, and nothing breaks it.
        await Assert.ThrowsAsync<PantsLeaseUnavailableException>(
            () => PantsDatabase.OpenAsync(CreateOptions(databasePath)).AsTask());
        Assert.True(File.Exists(lockPath));

        var recovered = await PantsDatabase.RecoverStaleLeaseMutationLockAsync(databasePath);

        Assert.True(recovered);
        Assert.False(File.Exists(lockPath));
        await using var database = await PantsDatabase.OpenAsync(CreateOptions(databasePath));
    }

    [Fact]
    public async Task ShouldReportNothingToRecoverWhenNoMutationLockExists()
    {
        using var directory = new TemporaryDirectory();

        var recovered = await PantsDatabase.RecoverStaleLeaseMutationLockAsync(directory.Path);

        Assert.False(recovered);
    }

    [Fact]
    public async Task ShouldRefuseRecoveryWhileLiveWriterLeaseIsStillValid()
    {
        using var directory = new TemporaryDirectory();
        using var lease = FileLease.Acquire(
            directory.Path,
            0,
            TimeSpan.Zero,
            null,
            LongHeartbeatInterval);
        var lockPath = Path.Combine(directory.Path, LockFileName);
        await File.WriteAllTextAsync(
            lockPath,
            "holder_id=interrupted\nowner_token=interrupted-token\ncreated_at=2020-01-01T00:00:00.0000000+00:00\n");

        await Assert.ThrowsAsync<PantsLeaseHeldException>(
            () => PantsDatabase.RecoverStaleLeaseMutationLockAsync(directory.Path).AsTask());

        Assert.Contains("interrupted-token", await File.ReadAllTextAsync(lockPath));
    }

    static async Task WaitForLockReleaseAsync(string lockPath)
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.Expected);
        while (true)
        {
            try
            {
                await using var stream = new FileStream(
                    lockPath,
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

    static PantsOpenOptions CreateOptions(string path) =>
        PantsOpenOptions.Local(path).WithBackgroundCompaction(false);
}
