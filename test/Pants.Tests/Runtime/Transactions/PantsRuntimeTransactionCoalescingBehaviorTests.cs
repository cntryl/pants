using System.Globalization;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime.Transactions;

public sealed class PantsRuntimeTransactionCoalescingBehaviorTests
{
    [Fact]
    public async Task ShouldPreserveEveryConcurrentWriteWhileRuntimeCoalesces()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new CoalescedCommitFailureFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false),
            new RuntimeDependencies(failpoints));
        const int commitCount = 16;
        var transactions = new List<IPantsTransaction>(commitCount);
        for (var index = 0; index < commitCount; index++)
        {
            var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(
                TestBytes.FromString($"coalesced-key-{index:D2}"),
                TestBytes.FromString($"coalesced-value-{index:D2}"));
            transactions.Add(transaction);
        }

        var barrier = database.Diagnostics.GetRuntimeMetricsAsync().AsTask();
        await failpoints.WaitForRuntimeBarrierAsync(TestTimeouts.Expected);
        var commits = transactions
            .Select(transaction => transaction.CommitAsync(PantsWriteOptions.Buffered).AsTask())
            .ToArray();
        failpoints.ReleaseRuntimeBarrier();
        _ = await barrier.WaitAsync(TestTimeouts.Expected);
        await Task.WhenAll(commits).WaitAsync(TestTimeouts.Expected);

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var rowCount = 0;
        await foreach (var _ in scan)
        {
            rowCount++;
        }

        Assert.Equal(commitCount, rowCount);
        Assert.Equal(1, metrics.WalAppendCount);
        Assert.Equal(0, metrics.WalFlushCount);
        Assert.Equal(0, metrics.WalFsyncCount);
        Assert.Equal(0, metrics.DurabilityWaitersFannedOutTotal);

        foreach (var transaction in transactions)
        {
            await transaction.DisposeAsync();
        }
    }

    [Fact]
    public async Task ShouldPublishBestEffortGroupWithoutWritingWalFrames()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new CoalescedCommitFailureFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false),
            new RuntimeDependencies(failpoints));
        var transactions = await CreateTransactionsAsync(database, "best-effort", 8);

        await CommitBehindRuntimeBarrierAsync(
            database,
            failpoints,
            transactions,
            PantsWriteOptions.BestEffort);

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(0, metrics.WalAppendCount);
        await AssertKeysVisibleAsync(database, "best-effort", transactions.Count);
        await DisposeTransactionsAsync(transactions);
    }

    [Fact]
    public async Task ShouldPublishCloudAsyncGroupAtOneLocalWalBoundary()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new CoalescedCommitFailureFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "coalescing/")
                .WithBackgroundCompaction(false),
            new RuntimeDependencies(failpoints));
        var transactions = await CreateTransactionsAsync(database, "cloud-async", 8);

        await CommitBehindRuntimeBarrierAsync(
            database,
            failpoints,
            transactions,
            PantsWriteOptions.CloudAsync);
        await DisposeTransactionsAsync(transactions);

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(1, metrics.WalAppendCount);
        Assert.Equal(0, metrics.WalFsyncCount);
        await AssertKeysVisibleAsync(database, "cloud-async", transactions.Count);
    }

    [Fact]
    public async Task ShouldMaintainOrderingAcrossSequentialRuntimeCommits()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        var family = await database.ColumnFamilies.CreateAsync("test_counter");
        const int operationCount = 100;
        for (var index = 0; index < operationCount; index++)
        {
            await CommitAsync(database, family, "counter", index.ToString(
                CultureInfo.InvariantCulture));
        }

        await using var reader = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        var finalValue = await reader.GetAsync("counter"u8.ToArray());

        Assert.Equal("99", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(finalValue)));
    }

    [Fact]
    public async Task ShouldReportUnknownAndFenceSuffixGivenPostDurabilityApplyFailure()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new CoalescedCommitFailureFailpointHandler(
            Failpoint.AfterCoalescedCommitOperationApply,
            failAtHit: 2);
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         options,
                         new RuntimeDependencies(failpoints)))
        {
            var transactions = await CreateTransactionsAsync(database, "apply-failure", 9);
            var barrier = database.Diagnostics.GetRuntimeMetricsAsync().AsTask();
            await failpoints.WaitForRuntimeBarrierAsync(TestTimeouts.Expected);
            var commits = transactions
                .Select((transaction, index) =>
                    transaction.CommitAsync(
                        index == transactions.Count - 1
                            ? PantsWriteOptions.Buffered
                            : PantsWriteOptions.Sync).AsTask())
                .ToArray();
            failpoints.ReleaseRuntimeBarrier();
            _ = await barrier.WaitAsync(TestTimeouts.Expected);

            foreach (var commit in commits[..^1])
            {
                var failure = await Assert.ThrowsAsync<PantsCommitOutcomeUnknownException>(
                    () => commit.WaitAsync(TestTimeouts.Expected));
                Assert.Equal(PantsErrorCode.CommitOutcomeUnknown, failure.Code);
                Assert.Equal(PantsErrorSeverity.Fatal, failure.Severity);
                Assert.IsType<PantsNoSpaceException>(failure.InnerException);
            }

            var suffixFailure = await Assert.ThrowsAsync<PantsFencedException>(
                () => commits[^1].WaitAsync(TestTimeouts.Expected));
            Assert.Equal(PantsErrorCode.Fenced, suffixFailure.Code);
            Assert.Contains("was not applied", suffixFailure.Message, StringComparison.OrdinalIgnoreCase);

            await using (var afterFailure = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                afterFailure.Put("after-apply-failure"u8.ToArray(), "value"u8.ToArray());
                var fenced = await Assert.ThrowsAsync<PantsFencedException>(() =>
                    afterFailure.CommitAsync(PantsWriteOptions.Sync).AsTask());
                Assert.Equal(PantsErrorCode.Fenced, fenced.Code);
                Assert.Contains("reopen", fenced.Message, StringComparison.OrdinalIgnoreCase);
            }

            var metrics = await database.Diagnostics.GetRuntimeMetricsAsync()
                .AsTask()
                .WaitAsync(TestTimeouts.Expected);
            Assert.Equal(PantsEngineHealth.Degraded, metrics.Health);
            Assert.Equal(1, metrics.WalAppendCount);
            Assert.Equal(1, metrics.WalFsyncCount);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily)
                .AsTask()
                .WaitAsync(TestTimeouts.Expected);
            await using (var reader = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadOnly))
            {
                for (var index = 0; index < 8; index++)
                {
                    Assert.Null(await reader.GetAsync(
                        TestBytes.FromString($"apply-failure-key-{index}")));
                }
            }

            await DisposeTransactionsAsync(transactions);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertKeysVisibleAsync(reopened, "apply-failure", 8);
        await using var read = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await read.GetAsync("apply-failure-key-8"u8.ToArray()));
        Assert.Null(await read.GetAsync("after-apply-failure"u8.ToArray()));
    }

    static async Task CommitAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key,
        string value)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
    }

    static async Task<List<IPantsTransaction>> CreateTransactionsAsync(
        IPantsDatabase database,
        string prefix,
        int count)
    {
        var transactions = new List<IPantsTransaction>(count);
        for (var index = 0; index < count; index++)
        {
            var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(
                TestBytes.FromString($"{prefix}-key-{index}"),
                TestBytes.FromString($"{prefix}-value-{index}"));
            transactions.Add(transaction);
        }

        return transactions;
    }

    static async Task CommitBehindRuntimeBarrierAsync(
        IPantsDatabase database,
        CoalescedCommitFailureFailpointHandler failpoints,
        IReadOnlyList<IPantsTransaction> transactions,
        PantsWriteOptions writeOptions)
    {
        var barrier = database.Diagnostics.GetRuntimeMetricsAsync().AsTask();
        await failpoints.WaitForRuntimeBarrierAsync(TestTimeouts.Expected);
        var commits = transactions
            .Select(transaction => transaction.CommitAsync(writeOptions).AsTask())
            .ToArray();
        failpoints.ReleaseRuntimeBarrier();
        _ = await barrier.WaitAsync(TestTimeouts.Expected);
        await Task.WhenAll(commits).WaitAsync(TestTimeouts.Expected);
    }

    static async Task AssertKeysVisibleAsync(
        IPantsDatabase database,
        string prefix,
        int count)
    {
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < count; index++)
        {
            Assert.NotNull(await reader.GetAsync(TestBytes.FromString($"{prefix}-key-{index}")));
        }
    }

    static async Task DisposeTransactionsAsync(IEnumerable<IPantsTransaction> transactions)
    {
        foreach (var transaction in transactions)
        {
            await transaction.DisposeAsync();
        }
    }
}
