using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class PantsCloudAssertionDurabilityTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldWaitForPendingCloudAsyncWriteGivenNonWritingCloudStrictCommit(
        bool assertValue)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using var failpoint = new BlockingCloudWalUploadFailpointHandler();
        var options = PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "assertion-durability/")
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                long.MaxValue,
                long.MaxValue,
                TimeSpan.FromHours(1),
                1024));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));
        await using (var writer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("key"u8.ToArray(), "value"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.CloudAsync);
        }

        var before = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(before.WalCloudDurableSequence < before.CurrentSequence);
        var walBefore = await File.ReadAllBytesAsync(Path.Combine(directory.Path, "wal", "wal.log"));
        await using var confirming = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        if (assertValue)
        {
            confirming.AssertValue("key"u8.ToArray(), "value"u8.ToArray());
        }

        // Act
        var commit = confirming.CommitAsync(PantsWriteOptions.CloudStrict).AsTask();
        var entered = failpoint.WaitUntilEnteredAsync(AssertionTimeout);
        try
        {
            Assert.Same(entered, await Task.WhenAny(commit, entered));
            await entered;
            Assert.False(commit.IsCompleted);
        }
        finally
        {
            failpoint.Release();
        }

        await commit.WaitAsync(AssertionTimeout);

        // Assert
        var after = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(before.CurrentSequence, after.CurrentSequence);
        Assert.Equal(after.CurrentSequence, after.WalCloudDurableSequence);
        var publishedWal = Assert.Single(Directory.GetFiles(
            Path.Combine(directory.Path, "cloud_store", "wal", "epochs"),
            "*.wal",
            SearchOption.AllDirectories));
        Assert.Equal(walBefore, await File.ReadAllBytesAsync(publishedWal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldNotSealWalGivenNonWritingCloudStrictCommitWhenAlreadyDurable(
        bool assertValue)
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.SimulatedCloud(
            directory.Path,
            "pants-tests",
            "already-durable/"));
        await using (var writer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("key"u8.ToArray(), "value"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.CloudStrict);
        }

        var before = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(before.CurrentSequence, before.WalCloudDurableSequence);
        var activeWalPath = Path.Combine(directory.Path, "wal", "wal.log");
        var walBefore = await File.ReadAllBytesAsync(activeWalPath);
        await using var confirming = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        if (assertValue)
        {
            confirming.AssertValue("key"u8.ToArray(), "value"u8.ToArray());
        }

        // Act
        await confirming.CommitAsync(PantsWriteOptions.CloudStrict);

        // Assert
        var after = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(before.CurrentSequence, after.CurrentSequence);
        Assert.Equal(before.CloudAsyncWalSegmentsSealed, after.CloudAsyncWalSegmentsSealed);
        Assert.Equal(before.CloudAsyncWalUploadsStarted, after.CloudAsyncWalUploadsStarted);
        Assert.Equal(walBefore, await File.ReadAllBytesAsync(activeWalPath));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "wal"), "*.wal"));
    }

    [Fact]
    public async Task ShouldJoinInflightUploadGivenAssertionOnlyCloudStrictCommit()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        using var failpoint = new BlockingCloudWalUploadFailpointHandler();
        var options = PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "inflight-upload/")
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                long.MaxValue,
                1,
                TimeSpan.FromHours(1),
                1024));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));
        await using (var writer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("key"u8.ToArray(), "value"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.CloudAsync);
        }

        await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(before.CloudAsyncWalSegmentsSealed > 0);
        await using var confirming = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        confirming.AssertValue("key"u8.ToArray(), "value"u8.ToArray());

        // Act
        var commit = confirming.CommitAsync(PantsWriteOptions.CloudStrict).AsTask();
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                commit.WaitAsync(TimeSpan.FromMilliseconds(500)));
        }
        finally
        {
            failpoint.Release();
        }

        await commit.WaitAsync(AssertionTimeout);

        // Assert
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(metrics.CurrentSequence, metrics.WalCloudDurableSequence);
        Assert.Equal(before.CloudAsyncWalSegmentsSealed, metrics.CloudAsyncWalSegmentsSealed);
    }

    [Fact]
    public async Task ShouldFailAssertionOnlyCloudStrictCommitGivenWalUploadFailure()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var failpoint = new PersistentThrowingFlushFailpointHandler(Failpoint.BeforeCloudWalUpload);
        var options = PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "failed-upload/")
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                long.MaxValue,
                long.MaxValue,
                TimeSpan.FromHours(1),
                1024));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));
        await using (var writer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("key"u8.ToArray(), "value"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.CloudAsync);
        }

        await using var confirming = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        confirming.AssertValue("key"u8.ToArray(), "value"u8.ToArray());

        // Act
        try
        {
            var failure = await Assert.ThrowsAsync<PantsIOException>(() =>
                confirming.CommitAsync(PantsWriteOptions.CloudStrict)
                    .AsTask()
                    .WaitAsync(AssertionTimeout));

            // Assert
            Assert.Contains("indeterminate", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(failpoint.HitCount > 0);
            var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.True(metrics.WalCloudDurableSequence < metrics.CurrentSequence);
        }
        finally
        {
            failpoint.Release();
        }
    }
}
