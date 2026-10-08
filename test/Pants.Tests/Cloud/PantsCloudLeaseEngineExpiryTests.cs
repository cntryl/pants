using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class PantsCloudLeaseEngineExpiryTests
{
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ShouldFenceEveryWritePathAndFireLossOnceWhenRenewalBlocksPastTheDeadline()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var losses = 0;
        var options = PantsOpenOptions.Cloud(cache.Path, CreateLocation())
            .WithBackgroundCompaction(false)
            .WithLeaseClockSkewTolerance(TimeSpan.FromMilliseconds(100))
            .WithLeaseTimeToLive(TimeSpan.FromSeconds(2))
            .WithLeaseLossCallback(() => Interlocked.Increment(ref losses));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(
                cloudHttpClient: client,
                leaseHeartbeatInterval: TimeSpan.FromMilliseconds(100)));
        await CommitAsync(database, "before", PantsWriteOptions.CloudStrict);
        Assert.True(database.PersistentStorage!.IsPrimaryLeaseHealthy);

        // Renewal now hangs; no caller does anything while the deadline passes.
        handler.BlockLeaseWrites();
        using var timeout = new CancellationTokenSource(Deadline);
        while (Volatile.Read(ref losses) == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        }

        Assert.Equal(1, Volatile.Read(ref losses));
        Assert.False(database.PersistentStorage.IsPrimaryLeaseHealthy);
        await Assert.ThrowsAsync<PantsFencedException>(() =>
            CommitAsync(database, "strict", PantsWriteOptions.CloudStrict));
        await Assert.ThrowsAsync<PantsFencedException>(() =>
            CommitAsync(database, "async", PantsWriteOptions.CloudAsync));
        await Assert.ThrowsAsync<PantsFencedException>(() =>
            database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask());
        await Assert.ThrowsAsync<PantsFencedException>(() =>
            database.Maintenance.CompactAllAsync().AsTask());

        // The hung renewal finally completes; authority must not come back.
        handler.ReleaseLeaseWrites();
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(database.PersistentStorage.IsPrimaryLeaseHealthy);
        Assert.Equal(1, Volatile.Read(ref losses));
        await Assert.ThrowsAsync<PantsFencedException>(() =>
            CommitAsync(database, "after-release", PantsWriteOptions.CloudStrict));
    }

    static async Task CommitAsync(IPantsDatabase database, string key, PantsWriteOptions writeOptions)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString("value"));
        await transaction.CommitAsync(writeOptions);
    }

    static PantsCloudStorageLocation CreateLocation() => new(
        new PantsAzureBlobProvider(
            "account",
            "container",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.SasToken("sig=test")),
        "database");
}
