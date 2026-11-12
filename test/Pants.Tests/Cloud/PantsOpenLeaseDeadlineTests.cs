using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class PantsOpenLeaseDeadlineTests
{
    [Fact]
    public async Task ShouldReturnPlainTimeoutAndNeverSubmitLeaseMutationWhenInitialReadFinishesLate()
    {
        using var cache = new TemporaryDirectory();
        using var remote = new TemporaryDirectory();
        var time = new ManualSchedulingTimeProvider();
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new HeldLeaseObjectStore(remote.Path, false, true);
        var location = new PantsCloudStorageLocation(new DelegatingCloudProvider(
            _ => ValueTask.FromResult<IPantsCloudObjectStore>(store)), "timed-open");
        var opening = PantsDatabase.OpenForTestingAsync(PantsOpenOptions.Cloud(cache.Path, location)
            .WithOpenTimeout(TimeSpan.FromSeconds(1)),
            new RuntimeDependencies(runtimeTimeProvider: time, startupObserver: startupEvent =>
            {
                if (startupEvent == StartupEvent.CleanupFinished) { cleaned.TrySetResult(); }
            })).AsTask();
        try
        {
            await store.Applied.WaitAsync(TestTimeouts.Expected);
            time.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<PantsTimeoutException>(() => opening.WaitAsync(TestTimeouts.Expected));
            Assert.Equal(0, store.LeasePuts);
        }
        finally
        {
            store.Release();
            await cleaned.Task.WaitAsync(TestTimeouts.Expected);
        }
        Assert.Equal(0, store.LeasePuts);
        Assert.Null(await new CloudObjectLeaseStore(store, PantsCloudObjectLayout.LeaseObjectKey).ReadAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ShouldRetainUnconfirmedLeaseCandidateAndReleaseOnlyItsOwnIdentity(
        bool loseResponse, bool successorTakesOver)
    {
        using var cache = new TemporaryDirectory();
        using var remote = new TemporaryDirectory();
        var time = new ManualSchedulingTimeProvider();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new HeldLeaseObjectStore(remote.Path, loseResponse);
        var provider = new DelegatingCloudProvider(_ => ValueTask.FromResult<IPantsCloudObjectStore>(store));
        var options = PantsOpenOptions.Cloud(cache.Path, new PantsCloudStorageLocation(provider, "timed-open"))
            .WithOpenTimeout(TimeSpan.FromSeconds(1)).WithLeaseClockSkewTolerance(TimeSpan.Zero)
            .WithBackgroundCompaction(false);
        var dependencies = new RuntimeDependencies(runtimeTimeProvider: time, leaseClock: clock,
            startupObserver: startupEvent =>
            {
                if (startupEvent == StartupEvent.CleanupFinished) { cleaned.TrySetResult(); }
            });
        var opening = PantsDatabase.OpenForTestingAsync(options, dependencies).AsTask();
        var leaseStore = new CloudObjectLeaseStore(store, PantsCloudObjectLayout.LeaseObjectKey);
        CloudLeaseRecord? successor = null;
        try
        {
            await store.Applied.WaitAsync(TestTimeouts.Expected);
            var original = Assert.IsType<CloudLeaseSnapshot>(await leaseStore.ReadAsync(CancellationToken.None));
            time.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<PantsLeaseIndeterminateException>(() => opening.WaitAsync(TestTimeouts.Expected));
            Assert.False(cleaned.Task.IsCompleted);
            if (successorTakesOver)
            {
                successor = original.Lease with { Epoch = original.Lease.Epoch + 1, HolderId = "successor", OwnerToken = "successor-token" };
                Assert.True(await leaseStore.TryReplaceAsync(original.Version, successor, CancellationToken.None));
            }
        }
        finally
        {
            store.Release();
            await cleaned.Task.WaitAsync(TestTimeouts.Expected);
        }

        var remaining = Assert.IsType<CloudLeaseSnapshot>(await leaseStore.ReadAsync(CancellationToken.None));
        if (successorTakesOver)
        {
            Assert.Equal(successor, remaining.Lease);
            await Assert.ThrowsAsync<PantsLeaseHeldException>(() => PantsDatabase.OpenForTestingAsync(options, dependencies).AsTask());
        }
        else
        {
            Assert.True(remaining.Lease.ExpiresAtUtc < clock.UtcNow);
            await using var reopened = await PantsDatabase.OpenForTestingAsync(options, dependencies);
            Assert.True(reopened.Cloud!.IsPrimaryLeaseHealthy);
            await using var write = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
            write.Put("accepted"u8.ToArray(), "value"u8.ToArray());
            await write.CommitAsync(PantsWriteOptions.CloudStrict);
        }
    }

    sealed class HeldLeaseObjectStore(string root, bool loseResponse, bool holdRead = false) : ICloudObjectStore
    {
        readonly SimulatedCloudObjectStore _inner = new(root);
        readonly TaskCompletionSource _applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _armed = 1;
        int _readArmed = holdRead ? 1 : 0;
        int _leasePuts;

        public Task Applied => _applied.Task;
        public int LeasePuts => Volatile.Read(ref _leasePuts);
        public void Release() => _release.TrySetResult();

        public async ValueTask<CloudObject?> GetAsync(string key, CancellationToken cancellationToken)
        {
            if (key == PantsCloudObjectLayout.LeaseObjectKey && Interlocked.Exchange(ref _readArmed, 0) != 0)
            {
                _applied.TrySetResult();
                await _release.Task;
            }
            return await _inner.GetAsync(key, holdRead ? CancellationToken.None : cancellationToken);
        }
        public ValueTask<CloudObjectMetadata?> HeadAsync(string key, CancellationToken cancellationToken) => _inner.HeadAsync(key, cancellationToken);
        public ValueTask<CloudObjectListPage> ListPageAsync(string prefix, string? continuation, CancellationToken cancellationToken) =>
            _inner.ListPageAsync(prefix, continuation, cancellationToken);
        public ValueTask<CloudObjectDeleteOutcome> DeleteAsync(string key, CloudObjectDeleteCondition condition, CancellationToken cancellationToken) =>
            _inner.DeleteAsync(key, condition, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async ValueTask<bool> PutAsync(string key, ReadOnlyMemory<byte> data,
            CloudObjectWriteCondition condition, CancellationToken cancellationToken)
        {
            if (key == PantsCloudObjectLayout.LeaseObjectKey) { Interlocked.Increment(ref _leasePuts); }
            var applied = await _inner.PutAsync(key, data, condition, cancellationToken);
            if (key == PantsCloudObjectLayout.LeaseObjectKey && applied && Interlocked.Exchange(ref _armed, 0) != 0)
            {
                _applied.TrySetResult();
                await _release.Task;
                if (loseResponse)
                {
                    throw new PantsIOException("The lease write landed but its response was lost.");
                }
            }
            return applied;
        }
    }
}
