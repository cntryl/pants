using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeWalUploadRetryParityTests
{
    [Fact]
    public async Task ShouldPreserveEarlierWritesWhenTheFirstWalSegmentUploadRetries()
    {
        using var cache = new TemporaryDirectory();
        using var freshCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var location = new PantsCloudStorageLocation(
            new PantsAzureBlobProvider(
                "account",
                "container",
                new Uri("https://storage.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=test")),
            "database");
        var dependencies = new RuntimeDependencies(cloudHttpClient: client);
        var options = PantsOpenOptions.Cloud(cache.Path, location)
            .WithBackgroundCompaction(false)
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                long.MaxValue,
                long.MaxValue,
                TimeSpan.FromHours(1),
                2));

        handler.FailWalSegmentWrites(segmentId: 1, attempts: 3);

        await using (var database = await PantsDatabase.OpenForTestingAsync(options, dependencies))
        {
            for (var index = 0; index < 6; index++)
            {
                await CommitCloudAsyncAsync(database, TestBytes.FromString($"midge-key-{index}"));
            }

            using (var timeout = new CancellationTokenSource(TestTimeouts.Expected))
            {
                while (handler.FailedTargetWalSegmentWriteAttempts < 3)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
                }
            }

            Assert.Equal(3, handler.FailedTargetWalSegmentWriteAttempts);
            await database.ShutdownAsync(TestTimeouts.Expected);
        }

        Assert.Equal(3, handler.FailedTargetWalSegmentWriteAttempts);

        await using var recovered = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(freshCache.Path, location).WithBackgroundCompaction(false),
            dependencies);
        await using var reader = await recovered.Transactions.BeginAsync(
            recovered.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        for (var index = 0; index < 6; index++)
        {
            Assert.Equal(
                "value",
                TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                    await reader.GetAsync(TestBytes.FromString($"midge-key-{index}")))));
        }
    }

    static async Task CommitCloudAsyncAsync(IPantsDatabase database, ReadOnlyMemory<byte> key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(key, "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
    }
}
