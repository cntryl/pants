using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class PantsCloudReplayBudgetTests
{
    const int MiB = 1024 * 1024;

    [Theory]
    [InlineData(PantsRecoveryPolicy.Strict)]
    [InlineData(PantsRecoveryPolicy.Salvage)]
    public async Task ShouldRejectAValidAcknowledgedCloudUnitBeyondTheRecoveryBudgetWithoutDiscardingIt(
        PantsRecoveryPolicy policy)
    {
        using var writerCache = new TemporaryDirectory();
        using var recoveryCache = new TemporaryDirectory();
        using var writerStore = new InMemoryAzureBlobHandler();
        using var writerClient = new HttpClient(writerStore);
        var writerClock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var value = Enumerable.Repeat((byte)'v', MiB).ToArray();
        InMemoryAzureBlobHandler image;

        await using (var writer = await PantsDatabase.OpenForTestingAsync(
                         Options(writerCache.Path, 64 * MiB, PantsRecoveryPolicy.Strict),
                         new RuntimeDependencies(cloudHttpClient: writerClient, leaseClock: writerClock)))
        {
            await using (var prefix = await writer.Transactions.BeginAsync(
                             writer.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
            {
                prefix.Put("prefix"u8.ToArray(), "stable"u8.ToArray());
                await prefix.CommitAsync(PantsWriteOptions.CloudStrict);
            }
            await using (var transaction = await writer.Transactions.BeginAsync(
                             writer.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
            {
                transaction.Put("large"u8.ToArray(), value);
                transaction.Put("same-unit"u8.ToArray(), "tail"u8.ToArray());
                var acknowledged = await transaction.CommitWithSequenceAsync(PantsWriteOptions.CloudStrict);
                Assert.True(await writer.Cloud!.GetDurableSequenceAsync() >= acknowledged);
            }

            // Capture the acknowledged WAL image while the writer is still live. Its
            // subsequent shutdown may publish SSTs only into writerStore, not this image.
            image = writerStore.CopyCommittedImage();
            Assert.DoesNotContain(image.GetObjectPaths("/sst/"), static path => path.EndsWith(".sst", StringComparison.Ordinal));
            Assert.True(image.WalStoredBytes > 0 && image.WalStoredBytes < value.Length);
        }

        using (image)
        using (var recoveryStore = image.CopyCommittedImage())
        using (var recoveryClient = new HttpClient(recoveryStore))
        {
            // The copied lease expires by the injected wall clock; no stored lease
            // document, WAL object, or committed pointer is changed by the fixture.
            var recoveryClock = new ManualClock(writerClock.UtcNow + TimeSpan.FromMinutes(2));
            var dependencies = new RuntimeDependencies(cloudHttpClient: recoveryClient, leaseClock: recoveryClock);
            var lowBudget = Options(recoveryCache.Path, 8 * MiB, policy);

            var failure = await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
                PantsDatabase.OpenForTestingAsync(lowBudget, dependencies).AsTask());
            Assert.Equal(PantsErrorCode.ResourceLimit, failure.Code);

            // Retry the same failed cache and object store. Resource pressure must not
            // salvage, quarantine, or retire the valid acknowledged unit or its prefix.
            await using var recovered = await PantsDatabase.OpenForTestingAsync(
                Options(recoveryCache.Path, 64 * MiB, policy), dependencies);
            await using var reader = await recovered.Transactions.BeginAsync(
                recovered.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
            Assert.Equal("stable"u8.ToArray(), (await reader.GetAsync("prefix"u8.ToArray()))?.ToArray());
            Assert.Equal(value, (await reader.GetAsync("large"u8.ToArray()))?.ToArray());
            Assert.Equal("tail"u8.ToArray(), (await reader.GetAsync("same-unit"u8.ToArray()))?.ToArray());
        }
    }

    static PantsOpenOptions Options(string path, int memoryBytes, PantsRecoveryPolicy policy) =>
        PantsOpenOptions.Cloud(path, new PantsCloudStorageLocation(
                new PantsAzureBlobProvider("account", "container", new Uri("https://storage.example.test"),
                    new PantsAzureCredentialSource.SasToken("sig=test")), "replay-budget"))
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(memoryBytes))
            .WithRecoveryPolicy(policy)
            .WithBackgroundCompaction(false)
            .WithLeaseTimeToLive(TimeSpan.FromSeconds(30))
            .WithLeaseClockSkewTolerance(TimeSpan.Zero)
            .WithStorageTimeout(TimeSpan.FromSeconds(5))
            .WithRuntimeResponseTimeout(TimeSpan.FromSeconds(10))
            .WithCloudWritePolicy(new PantsCloudWritePolicy(long.MaxValue, long.MaxValue, TimeSpan.FromHours(1), int.MaxValue));
}
