using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Contracts;

public sealed class PantsCloudWriteAdmissionParityTests
{
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
        await failpoint.WaitUntilEnteredAsync(TestTimeouts.Expected);

        try
        {
            await using var readOnly = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadOnly);
            await readOnly.CommitAsync(PantsWriteOptions.Sync)
                .AsTask()
                .WaitAsync(TestTimeouts.Expected);

            await using var empty = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            await empty.CommitAsync(PantsWriteOptions.CloudAsync)
                .AsTask()
                .WaitAsync(TestTimeouts.Expected);
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldValidateAssertionOnlyCommitWithoutAllocatingGivenCloudUploadQueueIsFull()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new FlushPipelineFailpointHandler(Failpoint.AfterCloudWalUpload);
        var options = CreateSaturatedQueueOptions(directory.Path, "assertion-only/");
        await using var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(failpoint));
        var family = database.ColumnFamilies.DefaultFamily;
        var startSequence = (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence;
        await using var assertionOnly = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite);
        assertionOnly.AssertValue("missing"u8.ToArray(), null);
        await using var conflicting = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite);
        conflicting.AssertValue("occupy-upload-queue"u8.ToArray(), null);
        await CommitWriteAsync(database, "occupy-upload-queue");
        await failpoint.WaitUntilEnteredAsync(TestTimeouts.Expected);
        ulong validatedCut = 0;
        try
        {
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.True(before.CurrentSequence > startSequence);
            Assert.True(before.WalCloudDurableSequence < before.CurrentSequence);
            validatedCut = await assertionOnly.CommitWithSequenceAsync(PantsWriteOptions.CloudAsync)
                .AsTask().WaitAsync(TestTimeouts.Expected);
            Assert.Equal(before.CurrentSequence, validatedCut);
            await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
                conflicting.CommitWithSequenceAsync(PantsWriteOptions.CloudAsync).AsTask().WaitAsync(TestTimeouts.Expected));

            await using (var mutation = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite))
            {
                mutation.Put("rejected-write"u8.ToArray(), "absent"u8.ToArray());
                await Assert.ThrowsAsync<PantsWriteStallException>(() =>
                    mutation.CommitWithSequenceAsync(PantsWriteOptions.CloudAsync).AsTask().WaitAsync(TestTimeouts.Expected));
            }
            await using (var reader = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly))
            {
                Assert.Equal("value"u8.ToArray(), (await reader.GetAsync("occupy-upload-queue"u8.ToArray()))?.ToArray());
                Assert.Null(await reader.GetAsync("missing"u8.ToArray()));
                Assert.Null(await reader.GetAsync("rejected-write"u8.ToArray()));
            }
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(before.CurrentSequence, after.CurrentSequence);
            Assert.Equal(before.WalAppendCount, after.WalAppendCount);
            Assert.Equal(before.WalBytesWrittenTotal, after.WalBytesWrittenTotal);
            Assert.Equal(before.ActiveMemtableBytes, after.ActiveMemtableBytes);
            Assert.Equal(before.WalCloudDurableSequence, after.WalCloudDurableSequence);
        }
        finally
        {
            failpoint.Release();
        }
        Assert.True(await database.Cloud!.WaitForDurableAsync(validatedCut, TestTimeouts.Expected));
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
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
    }
}
