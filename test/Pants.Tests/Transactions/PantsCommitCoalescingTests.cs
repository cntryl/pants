namespace Cntryl.Pants.Tests.Transactions;

public sealed class PantsCommitCoalescingTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShouldFanOutOneDurableSyncGivenConcurrentCommitsAndRecoverAll()
    {
        using var directory = new TemporaryDirectory();
        PantsRuntimeMetrics metrics;
        await using (var database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path)
                             .WithBackgroundCompaction(false)))
        {
            var transactions = new List<IPantsTransaction>();
            for (var index = 0; index < 32; index++)
            {
                var transaction = await database.BeginTransactionAsync(
                    database.DefaultColumnFamily,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(
                    TestBytes.FromString($"key-{index:D2}"),
                    TestBytes.FromString($"value-{index:D2}"));
                transactions.Add(transaction);
            }

            await Task.WhenAll(transactions.Select(transaction => Task.Run(async () =>
            {
                await transaction.CommitAsync(PantsWriteOptions.Sync);
                await transaction.DisposeAsync();
            })));
            metrics = await database.GetRuntimeMetricsAsync();
        }

        Assert.InRange(metrics.WalAppendCount, 1, 31);
        Assert.InRange(metrics.WalFsyncCount, 1, 31);
        Assert.True(metrics.DurabilityWaitersFannedOutTotal > 1);

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using var reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 32; index++)
        {
            var value = await reader.GetAsync(
                TestBytes.FromString($"key-{index:D2}"));
            Assert.NotNull(value);
            Assert.Equal($"value-{index:D2}", TestBytes.ToText(value.Value));
        }
    }

    [Fact]
    public async Task ShouldCoalesceBufferedCommitsAndRecoverGivenOrderlyClose()
    {
        const int commitCount = 8;
        using var directory = new TemporaryDirectory();
        using var failpoints = new CoalescedCommitSyncFailureFailpointHandler();
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false),
                         new PantsRuntimeDependencies(failpoints)))
        {
            var transactions = new List<IPantsTransaction>(commitCount);
            for (var index = 0; index < commitCount; index++)
            {
                var transaction = await database.BeginTransactionAsync(
                    database.DefaultColumnFamily,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(
                    TestBytes.FromString($"buffered-key-{index:D2}"),
                    TestBytes.FromString($"buffered-value-{index:D2}"));
                transactions.Add(transaction);
            }

            var barrier = database.GetRuntimeMetricsAsync().AsTask();
            await failpoints.WaitForRuntimeBarrierAsync(AssertionTimeout);
            var commits = transactions
                .Select(transaction => transaction.CommitAsync(PantsWriteOptions.Buffered).AsTask())
                .ToArray();
            failpoints.ReleaseRuntimeBarrier();
            var before = await barrier.WaitAsync(AssertionTimeout);
            await Task.WhenAll(commits).WaitAsync(AssertionTimeout);

            var metrics = await database.GetRuntimeMetricsAsync();
            Assert.Equal(before.WalAppendCount + 1, metrics.WalAppendCount);
            Assert.Equal(before.WalFlushCount, metrics.WalFlushCount);
            Assert.Equal(before.WalFsyncCount, metrics.WalFsyncCount);
            Assert.Equal(commitCount, metrics.WalPendingWrites);
            Assert.Equal(before.WalLastSyncedSequence, metrics.WalLastSyncedSequence);
            Assert.Equal(before.WalLocalDurableSequence, metrics.WalLocalDurableSequence);
            Assert.Equal(
                before.DurabilityWaitersFannedOutTotal,
                metrics.DurabilityWaitersFannedOutTotal);

            await using var reader = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadOnly);
            for (var index = 0; index < commitCount; index++)
            {
                Assert.Equal(
                    $"buffered-value-{index:D2}",
                    TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                        await reader.GetAsync(TestBytes.FromString($"buffered-key-{index:D2}")))));
            }

            foreach (var transaction in transactions)
            {
                await transaction.DisposeAsync();
            }
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        await using var reopenedReader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < commitCount; index++)
        {
            Assert.Equal(
                $"buffered-value-{index:D2}",
                TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                    await reopenedReader.GetAsync(TestBytes.FromString($"buffered-key-{index:D2}")))));
        }
    }

    [Fact]
    public async Task ShouldFsyncOnlyAtRotationGivenCoalescedBufferedGroup()
    {
        const int commitCount = 4;
        using var directory = new TemporaryDirectory();
        using var failpoints = new CoalescedCommitSyncFailureFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithBackgroundCompaction(false)
                .WithWalBufferSize(1),
            new PantsRuntimeDependencies(failpoints));
        var transactions = new List<IPantsTransaction>(commitCount);
        for (var index = 0; index < commitCount; index++)
        {
            var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(
                TestBytes.FromString($"rotated-buffered-key-{index:D2}"),
                TestBytes.FromString($"rotated-buffered-value-{index:D2}"));
            transactions.Add(transaction);
        }

        var barrier = database.GetRuntimeMetricsAsync().AsTask();
        await failpoints.WaitForRuntimeBarrierAsync(AssertionTimeout);
        var commits = transactions
            .Select(transaction => transaction.CommitAsync(PantsWriteOptions.Buffered).AsTask())
            .ToArray();
        failpoints.ReleaseRuntimeBarrier();
        var before = await barrier.WaitAsync(AssertionTimeout);
        await Task.WhenAll(commits).WaitAsync(AssertionTimeout);

        var metrics = await database.GetRuntimeMetricsAsync();
        Assert.Equal(before.WalAppendCount + 1, metrics.WalAppendCount);
        Assert.Equal(before.WalFlushCount, metrics.WalFlushCount);
        Assert.Equal(before.WalFsyncCount + 1, metrics.WalFsyncCount);
        Assert.Equal(0, metrics.WalPendingWrites);
        Assert.Equal(metrics.CurrentSequence, metrics.WalLastSyncedSequence);
        Assert.Equal(metrics.CurrentSequence, metrics.WalLocalDurableSequence);
        Assert.Equal(
            before.DurabilityWaitersFannedOutTotal,
            metrics.DurabilityWaitersFannedOutTotal);

        foreach (var transaction in transactions)
        {
            await transaction.DisposeAsync();
        }
    }

    [Fact]
    public async Task ShouldRestoreWalFrontiersGivenBufferedGroupRollsBack()
    {
        const int commitCount = 3;
        using var directory = new TemporaryDirectory();
        using var failpoints = new CoalescedCommitFailureFailpointHandler(
            PantsFailpoint.AfterWalAppend,
            2);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false),
                         new PantsRuntimeDependencies(failpoints)))
        {
            var transactions = new List<IPantsTransaction>(commitCount);
            for (var index = 0; index < commitCount; index++)
            {
                var transaction = await database.BeginTransactionAsync(
                    database.DefaultColumnFamily,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(
                    TestBytes.FromString($"rolled-back-buffered-key-{index}"),
                    TestBytes.FromString($"rolled-back-buffered-value-{index}"));
                transactions.Add(transaction);
            }

            var barrier = database.GetRuntimeMetricsAsync().AsTask();
            await failpoints.WaitForRuntimeBarrierAsync(AssertionTimeout);
            var commits = transactions
                .Select(transaction => transaction.CommitAsync(PantsWriteOptions.Buffered).AsTask())
                .ToArray();
            failpoints.ReleaseRuntimeBarrier();
            var before = await barrier.WaitAsync(AssertionTimeout);

            foreach (var commit in commits)
            {
                await Assert.ThrowsAsync<PantsNoSpaceException>(() => commit.WaitAsync(AssertionTimeout));
            }

            var metrics = await database.GetRuntimeMetricsAsync();
            Assert.Equal(before.WalAppendCount + 1, metrics.WalAppendCount);
            Assert.Equal(before.WalFlushCount, metrics.WalFlushCount);
            Assert.Equal(before.WalFsyncCount, metrics.WalFsyncCount);
            Assert.Equal(before.WalPendingWrites, metrics.WalPendingWrites);
            Assert.Equal(before.WalLastSyncedSequence, metrics.WalLastSyncedSequence);
            Assert.Equal(before.WalLocalDurableSequence, metrics.WalLocalDurableSequence);
            Assert.Equal(
                before.DurabilityWaitersFannedOutTotal,
                metrics.DurabilityWaitersFannedOutTotal);

            await using (var reader = await database.BeginTransactionAsync(
                             database.DefaultColumnFamily,
                             PantsTransactionMode.ReadOnly))
            {
                for (var index = 0; index < commitCount; index++)
                {
                    Assert.Null(await reader.GetAsync(
                        TestBytes.FromString($"rolled-back-buffered-key-{index}")));
                }
            }

            foreach (var transaction in transactions)
            {
                await transaction.DisposeAsync();
            }

            await using var accepted = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            accepted.Put("accepted-after-buffered-rollback"u8.ToArray(), "accepted"u8.ToArray());
            await accepted.CommitAsync(PantsWriteOptions.Sync);
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        await using var reopenedReader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < commitCount; index++)
        {
            Assert.Null(await reopenedReader.GetAsync(
                TestBytes.FromString($"rolled-back-buffered-key-{index}")));
        }

        Assert.Equal(
            "accepted",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reopenedReader.GetAsync("accepted-after-buffered-rollback"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldReportOnePhysicalWalAppendGivenOneCoalescedLocalGroup()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new CoalescedCommitFailureFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false),
            new PantsRuntimeDependencies(failpoints));
        var transactions = new List<IPantsTransaction>();
        for (var index = 0; index < 8; index++)
        {
            var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(
                TestBytes.FromString($"physical-append-key-{index}"),
                TestBytes.FromString($"physical-append-value-{index}"));
            transactions.Add(transaction);
        }

        var barrier = database.GetRuntimeMetricsAsync().AsTask();
        await failpoints.WaitForRuntimeBarrierAsync(AssertionTimeout);
        var commits = transactions
            .Select(transaction => transaction.CommitAsync(PantsWriteOptions.Sync).AsTask())
            .ToArray();
        failpoints.ReleaseRuntimeBarrier();
        _ = await barrier.WaitAsync(AssertionTimeout);
        await Task.WhenAll(commits).WaitAsync(AssertionTimeout);

        var metrics = await database.GetRuntimeMetricsAsync();
        Assert.Equal(1, metrics.WalAppendCount);
        Assert.Equal(0, metrics.WalFlushCount);
        Assert.Equal(1, metrics.WalFsyncCount);
        Assert.Equal(transactions.Count, metrics.DurabilityWaitersFannedOutTotal);

        foreach (var transaction in transactions)
        {
            await transaction.DisposeAsync();
        }
    }

    [Fact]
    public async Task ShouldKeepFailedCoalescedSyncCommitsInvisible()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new CoalescedCommitSyncFailureFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false),
            new PantsRuntimeDependencies(failpoints));
        var transactions = new List<IPantsTransaction>();
        for (var index = 0; index < 16; index++)
        {
            var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(
                TestBytes.FromString($"failed-key-{index:D2}"),
                TestBytes.FromString($"failed-value-{index:D2}"));
            transactions.Add(transaction);
        }

        var barrier = database.GetRuntimeMetricsAsync().AsTask();
        await failpoints.WaitForRuntimeBarrierAsync(AssertionTimeout);
        var commits = transactions
            .Select(transaction => transaction.CommitAsync(PantsWriteOptions.Sync).AsTask())
            .ToArray();
        failpoints.ReleaseRuntimeBarrier();
        _ = await barrier.WaitAsync(AssertionTimeout);

        foreach (var commit in commits)
        {
            await Assert.ThrowsAsync<PantsNoSpaceException>(() => commit.WaitAsync(AssertionTimeout));
        }

        var metrics = await database.GetRuntimeMetricsAsync();
        Assert.Equal(1, metrics.WalAppendCount);
        Assert.Equal(0, metrics.WalFlushCount);
        Assert.Equal(0, metrics.WalFsyncCount);
        Assert.Equal(1, metrics.NoSpaceEvents);
        Assert.Equal(commits.Length, metrics.WriteStallsNoSpaceTotal);

        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 16; index++)
        {
            Assert.Null(await reader.GetAsync(TestBytes.FromString($"failed-key-{index:D2}")));
        }

        foreach (var transaction in transactions)
        {
            await transaction.DisposeAsync();
        }
    }

    [Fact]
    public async Task ShouldExecuteStoppedBufferedCommitGivenCleanCoalescedGroupFailure()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new CoalescedCommitSyncFailureFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false),
            new PantsRuntimeDependencies(failpoints));
        await using var first = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        first.Put("failed-prefix-1"u8.ToArray(), "first"u8.ToArray());
        await using var second = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        second.Put("failed-prefix-2"u8.ToArray(), "second"u8.ToArray());
        await using var buffered = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        buffered.Put("buffered-suffix"u8.ToArray(), "accepted"u8.ToArray());

        var barrier = database.GetRuntimeMetricsAsync().AsTask();
        await failpoints.WaitForRuntimeBarrierAsync(AssertionTimeout);
        var firstCommit = first.CommitAsync(PantsWriteOptions.Sync).AsTask();
        var secondCommit = second.CommitAsync(PantsWriteOptions.Sync).AsTask();
        var bufferedCommit = buffered.CommitAsync(PantsWriteOptions.Buffered).AsTask();
        failpoints.ReleaseRuntimeBarrier();
        _ = await barrier.WaitAsync(AssertionTimeout);

        await Assert.ThrowsAsync<PantsNoSpaceException>(() => firstCommit.WaitAsync(AssertionTimeout));
        await Assert.ThrowsAsync<PantsNoSpaceException>(() => secondCommit.WaitAsync(AssertionTimeout));
        await bufferedCommit.WaitAsync(AssertionTimeout);

        var metrics = await database.GetRuntimeMetricsAsync();
        Assert.Equal(1, metrics.NoSpaceEvents);
        Assert.Equal(2, metrics.WriteStallsNoSpaceTotal);
        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync("failed-prefix-1"u8.ToArray()));
        Assert.Null(await reader.GetAsync("failed-prefix-2"u8.ToArray()));
        Assert.Equal(
            "accepted",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync("buffered-suffix"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldFailEveryPreparedMemberGivenNthWalAppendFails()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new CoalescedCommitFailureFailpointHandler(
            PantsFailpoint.AfterWalAppend,
            2);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false),
            new PantsRuntimeDependencies(failpoints));
        var transactions = new List<IPantsTransaction>();
        for (var index = 0; index < 3; index++)
        {
            var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(
                TestBytes.FromString($"append-failure-key-{index}"),
                TestBytes.FromString($"append-failure-value-{index}"));
            transactions.Add(transaction);
        }

        var barrier = database.GetRuntimeMetricsAsync().AsTask();
        await failpoints.WaitForRuntimeBarrierAsync(AssertionTimeout);
        var commits = transactions
            .Select(transaction => transaction.CommitAsync(PantsWriteOptions.Sync).AsTask())
            .ToArray();
        failpoints.ReleaseRuntimeBarrier();
        _ = await barrier.WaitAsync(AssertionTimeout);

        foreach (var commit in commits)
        {
            await Assert.ThrowsAsync<PantsNoSpaceException>(() => commit.WaitAsync(AssertionTimeout));
        }

        var metrics = await database.GetRuntimeMetricsAsync();
        Assert.Equal(1, metrics.WalAppendCount);
        Assert.Equal(0, metrics.WalFlushCount);
        Assert.Equal(0, metrics.WalFsyncCount);

        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < transactions.Count; index++)
        {
            Assert.Null(await reader.GetAsync(TestBytes.FromString($"append-failure-key-{index}")));
        }

        foreach (var transaction in transactions)
        {
            await transaction.DisposeAsync();
        }
    }

    [Fact]
    public async Task ShouldNotRecoverFailedCoalescedGroupGivenLaterSyncSucceeds()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new CoalescedCommitFailureFailpointHandler(
            PantsFailpoint.AfterWalAppend,
            2);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false),
                         new PantsRuntimeDependencies(failpoints)))
        {
            var transactions = new List<IPantsTransaction>();
            for (var index = 0; index < 3; index++)
            {
                var transaction = await database.BeginTransactionAsync(
                    database.DefaultColumnFamily,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(
                    TestBytes.FromString($"ghost-key-{index}"),
                    TestBytes.FromString($"ghost-value-{index}"));
                transactions.Add(transaction);
            }

            var barrier = database.GetRuntimeMetricsAsync().AsTask();
            await failpoints.WaitForRuntimeBarrierAsync(AssertionTimeout);
            var failedCommits = transactions
                .Select(transaction => transaction.CommitAsync(PantsWriteOptions.Sync).AsTask())
                .ToArray();
            failpoints.ReleaseRuntimeBarrier();
            _ = await barrier.WaitAsync(AssertionTimeout);

            foreach (var commit in failedCommits)
            {
                await Assert.ThrowsAsync<PantsNoSpaceException>(() => commit.WaitAsync(AssertionTimeout));
            }

            await using var accepted = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            accepted.Put("accepted-after-failure"u8.ToArray(), "accepted"u8.ToArray());
            await accepted.CommitAsync(PantsWriteOptions.Sync);
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        await using var reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 3; index++)
        {
            Assert.Null(await reader.GetAsync(TestBytes.FromString($"ghost-key-{index}")));
        }

        Assert.Equal(
            "accepted",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync("accepted-after-failure"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldNotHideLaterSyncBehindTornCoalescedWalFrame()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new CoalescedCommitFailureFailpointHandler(
            PantsFailpoint.MidWalAppend,
            2);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false),
                         new PantsRuntimeDependencies(failpoints)))
        {
            var transactions = new List<IPantsTransaction>();
            for (var index = 0; index < 3; index++)
            {
                var transaction = await database.BeginTransactionAsync(
                    database.DefaultColumnFamily,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(
                    TestBytes.FromString($"torn-key-{index}"),
                    TestBytes.FromString($"torn-value-{index}"));
                transactions.Add(transaction);
            }

            var barrier = database.GetRuntimeMetricsAsync().AsTask();
            await failpoints.WaitForRuntimeBarrierAsync(AssertionTimeout);
            var failedCommits = transactions
                .Select(transaction => transaction.CommitAsync(PantsWriteOptions.Sync).AsTask())
                .ToArray();
            failpoints.ReleaseRuntimeBarrier();
            _ = await barrier.WaitAsync(AssertionTimeout);

            foreach (var commit in failedCommits)
            {
                await Assert.ThrowsAsync<PantsNoSpaceException>(() => commit.WaitAsync(AssertionTimeout));
            }

            await using var accepted = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            accepted.Put("accepted-after-torn-group"u8.ToArray(), "accepted"u8.ToArray());
            await accepted.CommitAsync(PantsWriteOptions.Sync);
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        await using var reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 3; index++)
        {
            Assert.Null(await reader.GetAsync(TestBytes.FromString($"torn-key-{index}")));
        }

        Assert.Equal(
            "accepted",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync("accepted-after-torn-group"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldKeepSpilledCommitsOutOfResidentCoalescingPreflight()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new CoalescedCommitFailureFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithBackgroundCompaction(false)
                .WithMemoryBudget(PantsMemoryBudget.FromBytes(16 * 1_024))
                .WithMemtableLimits(4 * 1_024)
                .WithTransactionMemoryPool(1_024),
            new PantsRuntimeDependencies(failpoints));
        var transactions = new List<IPantsTransaction>();
        for (var transactionIndex = 0; transactionIndex < 2; transactionIndex++)
        {
            var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            for (var operationIndex = 0; operationIndex < 4; operationIndex++)
            {
                transaction.Put(
                    TestBytes.FromString($"spill-{transactionIndex}-{operationIndex}"),
                    new byte[900]);
            }

            transactions.Add(transaction);
        }

        Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));
        var barrier = database.GetRuntimeMetricsAsync().AsTask();
        await failpoints.WaitForRuntimeBarrierAsync(AssertionTimeout);
        var commits = transactions
            .Select(transaction => transaction.CommitAsync(PantsWriteOptions.Sync).AsTask())
            .ToArray();
        failpoints.ReleaseRuntimeBarrier();
        _ = await barrier.WaitAsync(AssertionTimeout);

        await Task.WhenAll(commits).WaitAsync(AssertionTimeout);
        var metrics = await database.GetRuntimeMetricsAsync();

        Assert.Equal(0, metrics.DurabilityWaitersFannedOutTotal);
        Assert.Equal(2, metrics.WalFsyncCount);
    }

    [Fact]
    public async Task ShouldExecuteLaterCommitGivenMiddlePreparedCommitHasStaleColumnFamily()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new CoalescedCommitFailureFailpointHandler();
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false),
                         new PantsRuntimeDependencies(failpoints)))
        {
            var staleFamily = await database.CreateColumnFamilyAsync("stale-middle");
            await using var first = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            first.Put("prepared-prefix"u8.ToArray(), "first"u8.ToArray());
            await using var stale = await database.BeginTransactionAsync(
                staleFamily,
                PantsTransactionMode.ReadWrite);
            stale.Put("stale"u8.ToArray(), "must-not-commit"u8.ToArray());
            await database.DropColumnFamilyAsync(staleFamily);
            await using var later = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            later.Put("queued-suffix"u8.ToArray(), "later"u8.ToArray());

            var barrier = database.GetRuntimeMetricsAsync().AsTask();
            await failpoints.WaitForRuntimeBarrierAsync(AssertionTimeout);
            var firstCommit = first.CommitAsync(PantsWriteOptions.Sync).AsTask();
            var staleCommit = stale.CommitAsync(PantsWriteOptions.Sync).AsTask();
            var laterCommit = later.CommitAsync(PantsWriteOptions.Sync).AsTask();
            failpoints.ReleaseRuntimeBarrier();
            _ = await barrier.WaitAsync(AssertionTimeout);

            await firstCommit.WaitAsync(AssertionTimeout);
            await Assert.ThrowsAsync<PantsInvalidArgumentException>(() => staleCommit.WaitAsync(AssertionTimeout));
            await laterCommit.WaitAsync(AssertionTimeout);
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        await using var reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "first",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync("prepared-prefix"u8.ToArray()))));
        Assert.Equal(
            "later",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync("queued-suffix"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldAcknowledgeDurableGroupGivenPostSyncHookFails()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new CoalescedCommitFailureFailpointHandler(
            PantsFailpoint.AfterCoalescedWalDurabilityBoundary);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false),
                         new PantsRuntimeDependencies(failpoints)))
        {
            var transactions = new List<IPantsTransaction>();
            for (var index = 0; index < 2; index++)
            {
                var transaction = await database.BeginTransactionAsync(
                    database.DefaultColumnFamily,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(
                    TestBytes.FromString($"post-sync-key-{index}"),
                    TestBytes.FromString($"post-sync-value-{index}"));
                transactions.Add(transaction);
            }

            var barrier = database.GetRuntimeMetricsAsync().AsTask();
            await failpoints.WaitForRuntimeBarrierAsync(AssertionTimeout);
            var commits = transactions
                .Select(transaction => transaction.CommitAsync(PantsWriteOptions.Sync).AsTask())
                .ToArray();
            failpoints.ReleaseRuntimeBarrier();
            _ = await barrier.WaitAsync(AssertionTimeout);

            await Task.WhenAll(commits).WaitAsync(AssertionTimeout);
            await using var reader = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadOnly);
            for (var index = 0; index < transactions.Count; index++)
            {
                Assert.Equal(
                    $"post-sync-value-{index}",
                    TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                        await reader.GetAsync(TestBytes.FromString($"post-sync-key-{index}")))));
            }

            foreach (var transaction in transactions)
            {
                await transaction.DisposeAsync();
            }
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        await using var reopenedReader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 2; index++)
        {
            Assert.Equal(
                $"post-sync-value-{index}",
                TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                    await reopenedReader.GetAsync(TestBytes.FromString($"post-sync-key-{index}")))));
        }
    }
}
