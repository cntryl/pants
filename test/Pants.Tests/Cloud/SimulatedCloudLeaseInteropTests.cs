using Cntryl.Pants.Cloud.Internal.Objects;
using Cntryl.Pants.Storage.Internal.IO;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class SimulatedCloudLeaseInteropTests
{
    [Fact]
    public async Task ShouldReleaseTheSharedLeaseForAnotherEngineAndRecoverItsCommittedValues()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.SimulatedCloud(directory.Path, "interop", "bounded-lease/")
            .WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await using var writer = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
            writer.Put("key"u8.ToArray(), "committed"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        var objects = new SimulatedCloudObjectStore(Path.Combine(directory.Path, "cloud_store"));
        using (var successor = new CloudLeaseCoordinator(
                   new CloudObjectLeaseStore(objects, PantsCloudObjectLayout.LeaseObjectKey),
                   new ManualClock(DateTimeOffset.UtcNow), "independent-engine", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15)))
        {
            Assert.True(await successor.AcquireAsync(CancellationToken.None) > 1);
            await successor.ReleaseAsync(CancellationToken.None);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("committed"u8.ToArray(), (await reader.GetAsync("key"u8.ToArray()))?.ToArray());
    }

    [Fact]
    public async Task ShouldEnforceConditionalMutationAfterAnIndependentNativeLockOwnerChangesTheObject()
    {
        using var directory = new TemporaryDirectory();
        var contended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new SimulatedCloudObjectStore(directory.Path, () => contended.TrySetResult());
        const string key = "metadata/lease";
        Assert.True(await store.PutAsync(key, "old"u8.ToArray(), new CloudObjectWriteCondition.IfAbsent()));
        var original = Assert.IsType<CloudObject>(await store.GetAsync(key));
        using var held = SimulatedCloudMutationLock.Acquire(
            directory.Path, Path.Combine(directory.Path, "metadata", "lease"), CancellationToken.None);
        var mutation = Task.Run(async () => await store.PutAsync(
            key, "stale"u8.ToArray(), new CloudObjectWriteCondition.IfVersion(original.Version)));
        try
        {
            var completed = await Task.WhenAny(mutation, contended.Task).WaitAsync(TestTimeouts.Expected);
            Assert.Same(contended.Task, completed);
            Assert.False(mutation.IsCompleted);
            Assert.Equal("old"u8.ToArray(), File.ReadAllBytes(Path.Combine(directory.Path, "metadata", "lease")));
            AtomicStagedFile.Write(Path.Combine(directory.Path, "metadata", "lease"), "successor"u8);
        }
        finally
        {
            held.Dispose();
            _ = await mutation.WaitAsync(TestTimeouts.Expected);
        }

        Assert.False(await mutation);
        Assert.Equal("successor"u8.ToArray(), (await store.GetAsync(key))?.Data.ToArray());
    }

    [Fact]
    public async Task ShouldCancelWaitingForTheSharedNativeMutationLockWithoutChangingTheObject()
    {
        using var directory = new TemporaryDirectory();
        var contended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new SimulatedCloudObjectStore(directory.Path, () => contended.TrySetResult());
        const string key = "metadata/lease";
        Assert.True(await store.PutAsync(key, "old"u8.ToArray(), new CloudObjectWriteCondition.IfAbsent()));
        using var held = SimulatedCloudMutationLock.Acquire(
            directory.Path, Path.Combine(directory.Path, "metadata", "lease"), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var mutation = Task.Run(async () => await store.PutAsync(
            key, "cancelled"u8.ToArray(), new CloudObjectWriteCondition.Unconditional(), cancellation.Token));
        try
        {
            var completed = await Task.WhenAny(mutation, contended.Task).WaitAsync(TestTimeouts.Expected);
            Assert.Same(contended.Task, completed);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        }
        finally
        {
            held.Dispose();
            try
            {
                _ = await mutation.WaitAsync(TestTimeouts.Expected);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Assert.Equal("old"u8.ToArray(), (await store.GetAsync(key))?.Data.ToArray());
    }
}
