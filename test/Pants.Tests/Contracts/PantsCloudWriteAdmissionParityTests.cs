namespace Cntryl.Pants.Tests.Contracts;

public sealed class PantsCloudWriteAdmissionParityTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShouldAllowNonWritingCommitsGivenCloudUploadQueueIsFull()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.AfterCloudWalUpload);
        var options = CreateSaturatedQueueOptions(directory.Path, "non-writing/");
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));
        await CommitWriteAsync(database, "occupy-upload-queue");
        await failpoint.WaitUntilEnteredAsync(AssertionTimeout);

        try
        {
            await using var readOnly = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadOnly);
            await readOnly.CommitAsync(PantsWriteOptions.Sync)
                .AsTask()
                .WaitAsync(AssertionTimeout);

            await using var empty = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            await empty.CommitAsync(PantsWriteOptions.CloudAsync)
                .AsTask()
                .WaitAsync(AssertionTimeout);
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldRejectAssertionOnlyCommitGivenCloudUploadQueueIsFull()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(
            Failpoint.AfterCloudWalUpload);
        var options = CreateSaturatedQueueOptions(directory.Path, "assertion-only/");
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));
        await CommitWriteAsync(database, "occupy-upload-queue");
        await failpoint.WaitUntilEnteredAsync(AssertionTimeout);

        try
        {
            await using var assertionOnly = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            assertionOnly.AssertValue("missing"u8.ToArray(), null);

            await Assert.ThrowsAsync<PantsWriteStallException>(() =>
                assertionOnly.CommitAsync(PantsWriteOptions.CloudAsync)
                    .AsTask()
                    .WaitAsync(AssertionTimeout));
        }
        finally
        {
            failpoint.Release();
        }
    }

    static PantsOpenOptions CreateSaturatedQueueOptions(string path, string prefix) =>
        PantsOpenOptions
            .SimulatedCloud(path, "pants-tests", $"cloud-write-admission/{prefix}")
            .WithCoordinatorQueueCapacityForTesting(1)
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                long.MaxValue,
                long.MaxValue,
                TimeSpan.FromHours(1),
                1))
            .WithBackgroundCompaction(false);

    static async ValueTask CommitWriteAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
    }
}
