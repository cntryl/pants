using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class PantsBufferedWalSyncWindowTests
{
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShouldFsyncBufferedCommitWithinTheMaximumDelayWithoutFurtherCallerActivity()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        var before = (await database.Diagnostics.GetRuntimeMetricsAsync()).WalLastSyncedSequence;

        await CommitBufferedAsync(database, "key", new byte[16]);

        await WaitForNewSyncAsync(database, before);
    }

    [Fact]
    public async Task ShouldFsyncBufferedCommitsPromptlyOnceTheByteThresholdIsReached()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        var before = (await database.Diagnostics.GetRuntimeMetricsAsync()).WalLastSyncedSequence;

        await CommitBufferedAsync(
            database,
            "large",
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(80 * 1024));

        await WaitForNewSyncAsync(database, before);
    }

    [Fact]
    public async Task ShouldFenceTheWalWhenTheTimedSyncFails()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new FailingSyncFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new RuntimeDependencies(failpoints));
        failpoints.Arm();

        await CommitBufferedAsync(database, "key", new byte[16]);
        await failpoints.WaitUntilFailedAsync(Deadline);

        await Assert.ThrowsAnyAsync<PantsException>(async () =>
            await CommitBufferedAsync(database, "after", new byte[16]));
    }

    static async Task CommitBufferedAsync(IPantsDatabase database, string key, byte[] value)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), value);
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
    }

    static async Task WaitForNewSyncAsync(IPantsDatabase database, long before)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while ((await database.Diagnostics.GetRuntimeMetricsAsync()).WalLastSyncedSequence <= before)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    sealed class FailingSyncFailpointHandler : IFailpointHandler
    {
        readonly TaskCompletionSource _failed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _armed;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public async Task WaitUntilFailedAsync(TimeSpan timeout) =>
            await _failed.Task.WaitAsync(timeout);

        public void Hit(Failpoint failpoint)
        {
            if (failpoint == Failpoint.BeforeWalSync && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _ = _failed.TrySetResult();
                throw new IOException("Injected timed WAL sync failure.");
            }
        }
    }
}
