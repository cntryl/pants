using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class SimulatedCloudReadAuthorityTests
{
    [Fact]
    public async Task ShouldFenceEveryRetainedReadPathWhenSimulatedSharedAuthorityChanges()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "read-authority/")
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenAsync(options);
        await using (var writer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
        {
            writer.Put("a"u8.ToArray(), "one"u8.ToArray());
            writer.Put("b"u8.ToArray(), "two"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        await using var retained = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        await using var own = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        own.Put("own"u8.ToArray(), "intent"u8.ToArray());
        await using var forward = await retained.ScanAsync(new PantsScanQuery());
        await using var reverse = await retained.ScanAsync(new PantsScanQuery { Direction = PantsScanDirection.Reverse });
        await using var empty = await retained.ScanAsync(new PantsScanQuery { Limit = 0 });
        await using var first = forward.GetAsyncEnumerator();
        await using var second = reverse.GetAsyncEnumerator();
        await using var none = empty.GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());

        var leaseStore = new CloudObjectLeaseStore(
            new SimulatedCloudObjectStore(Path.Combine(directory.Path, "cloud_store")), PantsCloudObjectLayout.LeaseObjectKey);
        var previous = (await leaseStore.ReadAsync(CancellationToken.None))!;
        Assert.True(await leaseStore.TryReplaceAsync(previous.Version,
            previous.Lease with { Epoch = previous.Lease.Epoch + 1, OwnerToken = Guid.NewGuid().ToString("D") },
            CancellationToken.None));

        await Assert.ThrowsAsync<PantsFencedException>(() => retained.GetAsync("a"u8.ToArray()).AsTask());
        await Assert.ThrowsAsync<PantsFencedException>(() => retained.GetAsync("missing"u8.ToArray()).AsTask());
        await Assert.ThrowsAsync<PantsFencedException>(() => own.GetAsync("own"u8.ToArray()).AsTask());
        await Assert.ThrowsAsync<PantsFencedException>(() => retained.GetWithDiagnosticsAsync("a"u8.ToArray()).AsTask());
        await Assert.ThrowsAsync<PantsFencedException>(() => retained.ScanAsync(new PantsScanQuery()).AsTask());
        foreach (var iterator in new[] { first, second, none })
        {
            var failure = await Assert.ThrowsAsync<PantsFencedException>(() => iterator.MoveNextAsync().AsTask());
            Assert.Same(failure, await Assert.ThrowsAsync<PantsFencedException>(() => iterator.MoveNextAsync().AsTask()));
        }

        foreach (var mode in new[] { PantsTransactionMode.ReadOnly, PantsTransactionMode.ReadWrite })
        {
            await Assert.ThrowsAsync<PantsFencedException>(() => database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily, mode).AsTask());
        }
    }
}
