using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsWriteBackpressureOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldNeverSubmitAfterCallerSlowdownExpiresOrIsCanceled(bool canceled)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualSchedulingTimeProvider();
        var submission = new CountingFailpointHandler(Failpoint.BeforeCommitSubmission);
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false)
            .WithStorageTimeout(TimeSpan.FromMilliseconds(1))
            .WithRuntimeResponseTimeout(TimeSpan.FromMilliseconds(100))
            .WithWriteBackpressure(new PantsWriteBackpressureConfiguration(
                L0SoftLimit: 1, MaximumAdmissionDelay: TimeSpan.FromSeconds(1)));
        await using (var database = await PantsDatabase.OpenForTestingAsync(options,
                         new RuntimeDependencies(submission, runtimeTimeProvider: clock)))
        {
            await PutAsync(database, database.ColumnFamilies.DefaultFamily, "seed");
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();
            var submittedBefore = submission.HitCount;
            using var cancellation = new CancellationTokenSource();
            await using var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("refused"u8.ToArray(), "value"u8.ToArray());
            var commit = transaction.CommitAsync(PantsWriteOptions.Sync, cancellation.Token).AsTask();
            Assert.False(commit.IsCompleted);
            Assert.True(clock.HasTimerDueIn(TimeSpan.FromMilliseconds(1)));

            if (canceled)
            {
                clock.Advance(TimeSpan.FromMilliseconds(50));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commit.WaitAsync(TestTimeouts.Expected));
            }
            else
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
                await Assert.ThrowsAsync<PantsTimeoutException>(() => commit.WaitAsync(TestTimeouts.Expected));
            }
            clock.Advance(TimeSpan.FromSeconds(2));
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(before.CurrentSequence, after.CurrentSequence);
            Assert.Equal(before.WalAppendCount, after.WalAppendCount);
            Assert.Equal(before.WalBytesWrittenTotal, after.WalBytesWrittenTotal);
            Assert.Equal(before.ManifestNextWalSequence, after.ManifestNextWalSequence);
            Assert.Equal(submittedBefore, submission.HitCount);
            Assert.Equal(before.WriteSlowdownCount + 1, after.WriteSlowdownCount);
            await AssertValueAsync(database, database.ColumnFamilies.DefaultFamily, "refused", false);
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertValueAsync(reopened, reopened.ColumnFamilies.DefaultFamily, "refused", false);
        await AssertValueAsync(reopened, reopened.ColumnFamilies.DefaultFamily, "seed", true);
    }

    [Fact]
    public async Task ShouldUseTheRemainingOriginalDeadlineAfterSlowdownBeforeAnAcceptedWalCompletes()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualSchedulingTimeProvider();
        using var held = new FlushPipelineFailpointHandler(Failpoint.BeforeWalAppend, armOnConstruction: false);
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false)
            .WithStorageTimeout(TimeSpan.FromMilliseconds(1))
            .WithRuntimeResponseTimeout(TimeSpan.FromMilliseconds(100))
            .WithWriteBackpressure(new PantsWriteBackpressureConfiguration(
                L0SoftLimit: 1, MaximumAdmissionDelay: TimeSpan.FromMilliseconds(40)));
        await using (var database = await PantsDatabase.OpenForTestingAsync(options,
                         new RuntimeDependencies(held, runtimeTimeProvider: clock)))
        {
            await PutAsync(database, database.ColumnFamilies.DefaultFamily, "seed");
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await using var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("accepted"u8.ToArray(), "value"u8.ToArray());
            held.Arm();
            var commit = transaction.CommitAsync(PantsWriteOptions.Sync).AsTask();
            try
            {
                Assert.True(clock.HasTimerDueIn(TimeSpan.FromMilliseconds(1)));
                clock.Advance(TimeSpan.FromMilliseconds(40));
                await held.WaitUntilEnteredAsync(TestTimeouts.Expected);

                clock.Advance(TimeSpan.FromMilliseconds(60));

                await Assert.ThrowsAsync<PantsCommitOutcomeUnknownException>(() => commit.WaitAsync(TestTimeouts.Expected));
            }
            finally
            {
                held.Release();
            }
            Assert.NotNull(await database.ColumnFamilies.GetAsync("default"));
            await AssertValueAsync(database, database.ColumnFamilies.DefaultFamily, "accepted", true);
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertValueAsync(reopened, reopened.ColumnFamilies.DefaultFamily, "accepted", true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldClearOnlyTheDroppedDebtorsPendingBytesWhileOldSnapshotRemainsReadable(bool simulatedCloud)
    {
        using var directory = new TemporaryDirectory();
        var options = (simulatedCloud
                ? PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "pending-drop/")
                : PantsOpenOptions.Local(directory.Path))
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 4, BackgroundEnabled: false))
            .WithWriteBackpressure(new PantsWriteBackpressureConfiguration(PendingCompactionHardLimitBytes: 2));
        await using var database = await PantsDatabase.OpenAsync(options);
        var debtor = await database.ColumnFamilies.CreateAsync("debtor");
        var unrelated = await database.ColumnFamilies.CreateAsync("unrelated");
        for (var index = 0; index < 4; index++)
        {
            await PutAsync(database, debtor, $"seed-{index}", simulatedCloud);
            await database.Maintenance.FlushAsync(debtor);
        }
        await using var snapshot = await database.Transactions.BeginAsync(debtor, PantsTransactionMode.ReadOnly);
        Assert.False(await database.Maintenance.WaitForWriteStallClearAsync(database.ColumnFamilies.DefaultFamily,
            TimeSpan.Zero));
        var waiter = database.Maintenance.WaitForWriteStallClearAsync(database.ColumnFamilies.DefaultFamily,
            TestTimeouts.Expected).AsTask();
        Assert.NotNull(await database.ColumnFamilies.GetAsync("debtor"));
        Assert.False(waiter.IsCompleted);

        await database.ColumnFamilies.DropAsync(unrelated);
        Assert.False(waiter.IsCompleted);
        Assert.True((await database.Diagnostics.GetRuntimeMetricsAsync()).EstimatedPendingCompactionBytes >= 2);
        await database.ColumnFamilies.DropAsync(debtor);

        Assert.True(await waiter.WaitAsync(TestTimeouts.Expected));
        Assert.Equal(0UL, (await database.Diagnostics.GetRuntimeMetricsAsync()).EstimatedPendingCompactionBytes);
        Assert.Equal("value"u8.ToArray(), (await snapshot.GetAsync("seed-0"u8.ToArray()))?.ToArray());
        await PutAsync(database, database.ColumnFamilies.DefaultFamily, "after", simulatedCloud);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldBypassHeldPhysicalOutputWhenHardPressureOrShutdownStarts(bool shutdown)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualSchedulingTimeProvider();
        var rateWait = new RateWaitObserver();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithMemtableLimits(8192, 4096)
            .WithBackgroundCompaction(false)
            .WithWriteBackpressure(new PantsWriteBackpressureConfiguration(BackgroundWriteBytesPerSecond: 1));
        await using (var database = await PantsDatabase.OpenForTestingAsync(options,
                         new RuntimeDependencies(rateWait, runtimeTimeProvider: clock)))
        {
            await PutAsync(database, database.ColumnFamilies.DefaultFamily, "seed");
            var flush = database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask();
            await rateWait.Entered.WaitAsync(TestTimeouts.Expected);
            Assert.False(flush.IsCompleted);
            Assert.Equal(0, clock.GetTimestamp());
            if (shutdown)
            {
                await database.ShutdownAsync(TestTimeouts.Expected);
                try
                {
                    await flush.WaitAsync(TestTimeouts.Expected);
                }
                catch (PantsBusyException)
                {
                }
            }
            else
            {
                for (var index = 0; index < 3; index++)
                {
                    var family = await database.ColumnFamilies.CreateAsync($"pressure-{index}");
                    await using var transaction = await database.Transactions.BeginAsync(family,
                        PantsTransactionMode.ReadWrite);
                    transaction.Put("pressure"u8.ToArray(), new byte[2800]);
                    await transaction.CommitAsync(PantsWriteOptions.Sync);
                }
                await flush.WaitAsync(TestTimeouts.Expected);
                var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
                Assert.True(metrics.SstBytesWrittenTotal > 0);
                Assert.Equal(0UL, metrics.BackgroundRateLimitedBytes);
                await database.ShutdownAsync(TestTimeouts.Expected);
            }
            Assert.Equal(0, clock.GetTimestamp());
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertValueAsync(reopened, reopened.ColumnFamilies.DefaultFamily, "seed", true);
    }

    [Fact]
    public async Task ShouldValidateAssertionOnlyCommitsWhileHardL0PressureRemainsHeld()
    {
        using var directory = new TemporaryDirectory();
        var seedOptions = PantsOpenOptions.Local(directory.Path).WithCompaction(
            new PantsCompactionConfiguration(L0FileCountTrigger: 100, BackgroundEnabled: false));
        await using (var seed = await PantsDatabase.OpenAsync(seedOptions))
        {
            for (var index = 0; index < 13; index++)
            {
                await PutAsync(seed, seed.ColumnFamilies.DefaultFamily, $"seed-{index}", forceRewrite: true);
                await seed.Maintenance.FlushAsync(seed.ColumnFamilies.DefaultFamily);
            }
        }
        using var held = new FlushPipelineFailpointHandler(Failpoint.AfterCompactionOutputDurable);
        var options = seedOptions.WithCompaction(
            new PantsCompactionConfiguration(L0FileCountTrigger: 2, BackgroundEnabled: false));
        await using var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(held));
        try
        {
            await held.WaitUntilEnteredAsync(TestTimeouts.Expected);
            Assert.False(await database.Maintenance.WaitForWriteStallClearAsync(database.ColumnFamilies.DefaultFamily,
                TimeSpan.Zero));
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();
            await using (var valid = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                valid.AssertValue("seed-0"u8.ToArray(), "value"u8.ToArray());
                await valid.CommitAsync(PantsWriteOptions.Sync);
            }
            await using (var invalid = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                invalid.AssertValue("seed-0"u8.ToArray(), null);
                await Assert.ThrowsAsync<PantsWriteConflictException>(() => invalid.CommitAsync(PantsWriteOptions.Sync).AsTask());
            }
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(before.CurrentSequence, after.CurrentSequence);
            Assert.Equal(before.WalAppendCount, after.WalAppendCount);
            Assert.False(await database.Maintenance.WaitForWriteStallClearAsync(database.ColumnFamilies.DefaultFamily,
                TimeSpan.Zero));
        }
        finally
        {
            held.Release();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldUseOnlyOperationFamiliesForL0ButGlobalAdmittedBytesForMemory(bool memorySoft)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualSchedulingTimeProvider();
        var policy = memorySoft
            ? new PantsWriteBackpressureConfiguration(MemorySoftLimitBytes: 1,
                MaximumAdmissionDelay: TimeSpan.FromMilliseconds(20))
            : new PantsWriteBackpressureConfiguration(L0SoftLimit: 1,
                MaximumAdmissionDelay: TimeSpan.FromMilliseconds(20));
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false).WithWriteBackpressure(policy);
        await using var database = await PantsDatabase.OpenForTestingAsync(options,
            new RuntimeDependencies(runtimeTimeProvider: clock));
        var debtor = await database.ColumnFamilies.CreateAsync("debtor");
        await PutAsync(database, debtor, "seed");
        if (!memorySoft)
        {
            await database.Maintenance.FlushAsync(debtor);
        }
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();
        await using (var asserted = await database.Transactions.BeginAsync(debtor, PantsTransactionMode.ReadWrite))
        {
            asserted.AssertValue("seed"u8.ToArray(), "value"u8.ToArray());
            await asserted.CommitAsync(PantsWriteOptions.Sync);
        }
        await using var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("other"u8.ToArray(), "value"u8.ToArray());
        var commit = transaction.CommitAsync(PantsWriteOptions.Sync).AsTask();
        if (memorySoft)
        {
            Assert.False(commit.IsCompleted);
            Assert.True(clock.HasTimerDueIn(TimeSpan.FromMilliseconds(1)));
            clock.Advance(TimeSpan.FromMilliseconds(20));
        }
        await commit.WaitAsync(TestTimeouts.Expected);

        var after = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(before.WriteSlowdownCount + (memorySoft ? 1 : 0), after.WriteSlowdownCount);
        Assert.Equal(memorySoft ? TimeSpan.FromMilliseconds(20).Ticks * 100 : 0,
            after.WriteSlowdownNanosecondsTotal - before.WriteSlowdownNanosecondsTotal);
        await AssertValueAsync(database, database.ColumnFamilies.DefaultFamily, "other", true);
    }

    [Fact]
    public async Task ShouldFenceCallerSlowdownBeforeCommitSubmissionWhenCloudAuthorityChanges()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualSchedulingTimeProvider();
        var submission = new CountingFailpointHandler(Failpoint.BeforeCommitSubmission);
        var options = PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "slowdown-fence/")
            .WithBackgroundCompaction(false)
            .WithWriteBackpressure(new PantsWriteBackpressureConfiguration(L0SoftLimit: 1,
                MaximumAdmissionDelay: TimeSpan.FromSeconds(1)));
        await using var database = await PantsDatabase.OpenForTestingAsync(options,
            new RuntimeDependencies(submission, runtimeTimeProvider: clock));
        await PutAsync(database, database.ColumnFamilies.DefaultFamily, "seed", true);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var before = submission.HitCount;
        await using var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("refused"u8.ToArray(), "value"u8.ToArray());
        var commit = transaction.CommitAsync(PantsWriteOptions.CloudStrict).AsTask();
        Assert.False(commit.IsCompleted);
        var leaseStore = new CloudObjectLeaseStore(
            new SimulatedCloudObjectStore(Path.Combine(directory.Path, "cloud_store")), PantsCloudObjectLayout.LeaseObjectKey);
        var previous = (await leaseStore.ReadAsync(CancellationToken.None))!;
        Assert.True(await leaseStore.TryReplaceAsync(previous.Version,
            previous.Lease with { Epoch = previous.Lease.Epoch + 1, OwnerToken = Guid.NewGuid().ToString("D") },
            CancellationToken.None));

        clock.Advance(TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<PantsFencedException>(() => commit.WaitAsync(TestTimeouts.Expected));
        Assert.Equal(before, submission.HitCount);
    }

    [Fact]
    public async Task ShouldKeepRateLimitedPhysicalFlushOwnedAfterItsCallerCancels()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualSchedulingTimeProvider();
        var rate = new AdvancingRateWaitObserver(clock);
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false)
            .WithWriteBackpressure(new PantsWriteBackpressureConfiguration(BackgroundWriteBytesPerSecond: 1024 * 1024));
        await using (var database = await PantsDatabase.OpenForTestingAsync(options,
                         new RuntimeDependencies(rate, runtimeTimeProvider: clock)))
        {
            await PutAsync(database, database.ColumnFamilies.DefaultFamily, "seed");
            using var cancellation = new CancellationTokenSource();
            var flush = database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily, cancellation.Token).AsTask();
            try
            {
                await rate.Entered.WaitAsync(TestTimeouts.Expected);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flush.WaitAsync(TestTimeouts.Expected));
                var held = await database.Diagnostics.GetRuntimeMetricsAsync();
                Assert.Equal(1, held.ImmutableMemtables);
                Assert.True(held.ImmutableMemtableBytes > 0);
                Assert.Equal(0, held.SstBytesWrittenTotal);
                Assert.Equal(0UL, held.BackgroundRateLimitedBytes);
                Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(directory.Path, "sst", ".flush-staging")));
                await AssertValueAsync(database, database.ColumnFamilies.DefaultFamily, "seed", true);
            }
            finally
            {
                rate.Continue();
            }
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            var completed = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(0, completed.ImmutableMemtables);
            Assert.True(completed.SstBytesWrittenTotal > 0);
            Assert.Equal(checked((ulong)completed.SstBytesWrittenTotal), completed.BackgroundRateLimitedBytes);
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertValueAsync(reopened, reopened.ColumnFamilies.DefaultFamily, "seed", true);
    }

    static async Task PutAsync(IPantsDatabase database, IPantsColumnFamily family, string key, bool cloud = false,
        bool forceRewrite = false)
    {
        await using var transaction = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), "value"u8.ToArray());
        if (forceRewrite)
        {
            // The test observes physical output; overlapping empty keyspace prevents promotion.
            transaction.DeleteRange("!overlap/a"u8.ToArray(), "!overlap/z"u8.ToArray());
        }
        await transaction.CommitAsync(cloud ? PantsWriteOptions.CloudStrict : PantsWriteOptions.Sync);
    }

    static async Task AssertValueAsync(IPantsDatabase database, IPantsColumnFamily family, string key, bool present)
    {
        await using var read = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly);
        Assert.Equal(present ? "value"u8.ToArray() : null, (await read.GetAsync(TestBytes.FromString(key)))?.ToArray());
    }

    sealed class AdvancingRateWaitObserver(ManualSchedulingTimeProvider clock) : IFailpointHandler
    {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _continuing;
        public Task Entered => _entered.Task;

        public void Continue()
        {
            Volatile.Write(ref _continuing, 1);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        public void Hit(Failpoint failpoint)
        {
            if (failpoint != Failpoint.BeforeBackgroundWriteRateWait)
            {
                return;
            }
            _entered.TrySetResult();
            if (Volatile.Read(ref _continuing) != 0)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
            }
        }
    }

    sealed class RateWaitObserver : IFailpointHandler
    {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public void Hit(Failpoint failpoint)
        {
            if (failpoint == Failpoint.BeforeBackgroundWriteRateWait)
            {
                _entered.TrySetResult();
            }
        }
    }
}
