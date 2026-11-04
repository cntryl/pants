using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsCommitPublicationMaintenanceTests
{
    [Fact]
    public async Task ShouldRetryCollectionWhenLastSnapshotClosesDuringAnEarlierCollection()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new CollectionBarrier(Failpoint.AfterPublicationGarbageCollectionWork);
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(failpoints));
        await PutAsync(database, "version", "initial");
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var retainedFile = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        await using var snapshot = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        await OverwriteAndCompactAsync(database, "new");
        await using var empty = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        failpoints.Arm();
        await empty.RollbackAsync();
        await failpoints.Entered.WaitAsync(TestTimeouts.Expected);
        try
        {
            Assert.True(File.Exists(retainedFile));
            await snapshot.DisposeAsync();
        }
        finally
        {
            failpoints.Release();
        }

        // The first barrier can precede the successor enqueued by the paused collection.
        _ = await database.Diagnostics.GetStorageLayoutAsync();
        _ = await database.Diagnostics.GetStorageLayoutAsync();
        Assert.False(File.Exists(retainedFile));
        Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).PinnedSsts);
    }

    [Fact]
    public async Task ShouldCollectNewlyRetiredFilesWhileAnEarlierCollectionResponseIsPending()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new CollectionBarrier(Failpoint.AfterPublicationGarbageCollectionResponse);
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(failpoints));
        await PutAsync(database, "version", "initial");
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        await using var firstSnapshot = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        await OverwriteAndCompactAsync(database, "first");
        _ = await database.Diagnostics.GetStorageLayoutAsync();
        failpoints.Arm();
        await firstSnapshot.DisposeAsync();
        await failpoints.Entered.WaitAsync(TestTimeouts.Expected);
        try
        {
            var retainedFile = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
            await using var secondSnapshot = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
            await OverwriteAndCompactAsync(database, "second");
            Assert.True(File.Exists(retainedFile));
            await secondSnapshot.DisposeAsync();
            _ = await database.Diagnostics.GetStorageLayoutAsync();
            Assert.False(File.Exists(retainedFile));
            Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).PinnedSsts);
        }
        finally
        {
            failpoints.Release();
        }
    }

    static async Task OverwriteAndCompactAsync(IPantsDatabase database, string prefix)
    {
        for (var generation = 1; generation <= 2; generation++)
        {
            await PutAsync(database, "version", $"{prefix}-{generation}");
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        await database.Maintenance.CompactAllAsync();
    }

    [Fact]
    public async Task ShouldKeepCommittedReplySuccessfulWhenSnapshotPublicationReleasesFailingGarbageCollection()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new ReleaseBeforePublication();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(failpoints)))
        {
            await PutAsync(database, "version", "old");
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await using var pinned = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
            for (var generation = 1; generation <= 2; generation++)
            {
                await PutAsync(database, "version", $"new-{generation}");
                await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            }

            await database.Maintenance.CompactAllAsync();
            Assert.True((await database.Diagnostics.GetRuntimeMetricsAsync()).PinnedSsts > 0);
            failpoints.Snapshot = pinned;
            var commitFailure = await Record.ExceptionAsync(() => PutAsync(database, "accepted", "value"));
            if (failpoints.Release is { } release)
            {
                _ = await Record.ExceptionAsync(() => release);
            }
            _ = await database.Diagnostics.GetStorageLayoutAsync();

            await using var current = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
            Assert.Equal("value"u8.ToArray(), (await current.GetAsync("accepted"u8.ToArray()))?.ToArray());
            Assert.Equal(1, failpoints.FailureCount);
            Assert.Null(commitFailure);
            Assert.Equal(PantsEngineHealth.Degraded, (await database.Diagnostics.GetRuntimeMetricsAsync()).Health);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("value"u8.ToArray(), (await reader.GetAsync("accepted"u8.ToArray()))?.ToArray());
    }

    static async Task PutAsync(IPantsDatabase database, string key, string value)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    sealed class ReleaseBeforePublication : IFailpointHandler
    {
        bool _failJournal;

        public IPantsTransaction? Snapshot { get; set; }
        public Task? Release { get; private set; }
        public int FailureCount { get; private set; }

        public void Hit(Failpoint failpoint)
        {
            if (failpoint == Failpoint.BeforeCommitApply && Snapshot is { } snapshot)
            {
                Snapshot = null;
                Release = snapshot.DisposeAsync().AsTask();
                _failJournal = true;
            }
            else if (failpoint == Failpoint.BeforeManifestJournalAppend && _failJournal)
            {
                _failJournal = false;
                FailureCount++;
                throw new IOException("Injected garbage-collection journal failure after commit publication.");
            }
        }
    }

    sealed class CollectionBarrier(Failpoint target) : IFailpointHandler, IDisposable
    {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly ManualResetEventSlim _release = new();
        int _armed;

        public Task Entered => _entered.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _release.Set();

        public void Hit(Failpoint failpoint)
        {
            if (failpoint == target &&
                Interlocked.Exchange(ref _armed, 0) != 0)
            {
                _entered.TrySetResult();
                if (!_release.Wait(TestTimeouts.Expected))
                {
                    throw new TimeoutException("The publication collection response was not released.");
                }
            }
        }

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
        }
    }
}
