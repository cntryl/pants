using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsRuntimeResponseTimeoutTests
{
    [Fact]
    public void ShouldDeriveRuntimeResponseTimeoutFromStorageTimeout()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            RuntimePlan.Resolve(PantsOpenOptions.InMemory()).RuntimeResponseTimeout);
        Assert.Equal(
            TimeSpan.FromSeconds(75),
            RuntimePlan.Resolve(PantsOpenOptions.InMemory()
                .WithStorageTimeout(TimeSpan.FromSeconds(45))).RuntimeResponseTimeout);
        Assert.Equal(
            TimeSpan.MaxValue,
            RuntimePlan.Resolve(PantsOpenOptions.InMemory()
                .WithStorageTimeout(TimeSpan.MaxValue)).RuntimeResponseTimeout);
    }

    [Fact]
    public void ShouldPreserveExplicitRuntimeResponseTimeoutWhenStorageTimeoutChanges()
    {
        var options = PantsOpenOptions.InMemory()
            .WithRuntimeResponseTimeout(TimeSpan.FromSeconds(90))
            .WithStorageTimeout(TimeSpan.FromSeconds(45));

        Assert.Equal(TimeSpan.FromSeconds(90), RuntimePlan.Resolve(options).RuntimeResponseTimeout);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void ShouldRejectInvalidRuntimeResponseTimeoutRelativeToStorageTimeout(
        int responseTimeoutMilliseconds)
    {
        var exception = Assert.Throws<PantsInvalidArgumentException>(() =>
            PantsOpenOptions.InMemory()
                .WithStorageTimeout(TimeSpan.FromMilliseconds(1))
                .WithRuntimeResponseTimeout(TimeSpan.FromMilliseconds(responseTimeoutMilliseconds)));

        Assert.Contains("RuntimeResponseTimeout", exception.Message, StringComparison.Ordinal);
        Assert.Contains("StorageTimeout", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldBoundAdmittedRuntimeResponseAndClassifyLateCompletion()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new RuntimeMetricsResponseFailpointHandler();
        var time = new ManualSchedulingTimeProvider();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithBackgroundCompaction(false)
                .WithStorageTimeout(TimeSpan.FromMilliseconds(5))
                .WithRuntimeResponseTimeout(TimeSpan.FromMilliseconds(100)),
            new RuntimeDependencies(failpoint, runtimeTimeProvider: time));
        // Startup queues its own recovered-flush/L0 commands before reading the mailbox.
        // Crossing this owner barrier orders them ahead of the deliberately held request.
        _ = await database.Diagnostics.GetStorageLayoutAsync().AsTask().WaitAsync(TestTimeouts.Expected);
        var request = database.Diagnostics.GetRuntimeMetricsAsync().AsTask();

        try
        {
            await failpoint.WaitUntilEnteredAsync(TestTimeouts.Expected);
            time.Advance(TimeSpan.FromMilliseconds(99));
            Assert.False(request.IsCompleted);
            time.Advance(TimeSpan.FromMilliseconds(1));
            var exception = await Assert.ThrowsAsync<PantsTimeoutException>(() =>
                request.WaitAsync(TestTimeouts.Expected));

            Assert.Contains("GetRuntimeMetricsAsync", exception.Message, StringComparison.Ordinal);
            Assert.Contains("request ID", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("00:00:00.100", exception.Message, StringComparison.Ordinal);
            Assert.Contains("outcome is unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            failpoint.Release();
        }

        // The owner completes the held response before executing this next command. Its
        // confirmation budget uses the same stopped clock, so observation cannot add timeouts.
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync().AsTask()
            .WaitAsync(TestTimeouts.Expected);
        Assert.Equal(0, metrics.RuntimeResponseWaiters);
        Assert.Equal(1, metrics.RuntimeAbandonedRequestsTotal);
        Assert.Equal(1, metrics.RuntimeLateResponsesTotal);
        Assert.Equal(0, metrics.RuntimeAbandonedRequestMetadata);
    }

    [Fact]
    public async Task ShouldPersistAcceptedCommitAfterRuntimeResponseTimesOut()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new WriteAdmissionRaceFailpointHandler();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(512 * 1024))
            .WithMemtableLimits(2 * 1024)
            .WithTransactionMemoryPool(1024)
            .WithStorageTimeout(TimeSpan.FromMilliseconds(5))
            .WithRuntimeResponseTimeout(TimeSpan.FromMilliseconds(100));
        var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));

        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("accepted"u8.ToArray(), "durable"u8.ToArray());
            transaction.Put("spill-a"u8.ToArray(), new byte[900]);
            transaction.Put("spill-b"u8.ToArray(), new byte[900]);
            Assert.NotEmpty(Directory.GetFiles(
                Path.Combine(directory.Path, "txn"),
                "*.run"));
            failpoint.ArmWalAppend();
            var commit = transaction.CommitAsync(PantsWriteOptions.Sync).AsTask();
            await failpoint.WaitForWalAsync(TestTimeouts.Expected);

            try
            {
                var exception = await Assert.ThrowsAsync<PantsCommitOutcomeUnknownException>(() =>
                    commit.WaitAsync(TestTimeouts.Expected));
                Assert.Equal(PantsErrorCode.CommitOutcomeUnknown, exception.Code);
                Assert.Equal(PantsErrorSeverity.OutcomeUnknown, exception.Severity);
                Assert.Contains("outcome is unknown", exception.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                failpoint.ReleaseWal();
            }
        }

        await database.ShutdownAsync(TestTimeouts.Expected);
        await database.DisposeAsync();

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var read = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "durable"u8.ToArray(),
            (await read.GetAsync("accepted"u8.ToArray()))?.ToArray());
    }

    [Fact]
    public async Task ShouldReportUnknownCommitOutcomeGivenCallerCancelsAfterAdmission()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new WriteAdmissionRaceFailpointHandler();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithStorageTimeout(TimeSpan.FromSeconds(1))
            .WithRuntimeResponseTimeout(TimeSpan.FromSeconds(10));
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         options,
                         new RuntimeDependencies(failpoint)))
        using (var cancellation = new CancellationTokenSource())
        {
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("cancelled-accepted"u8.ToArray(), "durable"u8.ToArray());
            failpoint.ArmWalAppend();
            var commit = transaction.CommitAsync(PantsWriteOptions.Sync, cancellation.Token).AsTask();
            await failpoint.WaitForWalAsync(TestTimeouts.Expected);

            cancellation.Cancel();
            failpoint.ReleaseWal();
            var exception = await Record.ExceptionAsync(() => commit.WaitAsync(TestTimeouts.Expected));
            var unknown = Assert.IsType<PantsCommitOutcomeUnknownException>(exception);
            Assert.Equal(PantsErrorCode.CommitOutcomeUnknown, unknown.Code);
            Assert.Equal(PantsErrorSeverity.OutcomeUnknown, unknown.Severity);
            Assert.IsAssignableFrom<OperationCanceledException>(unknown.InnerException);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "durable",
            TestBytes.ToText((await reader.GetAsync("cancelled-accepted"u8.ToArray()))!.Value));
    }

}
