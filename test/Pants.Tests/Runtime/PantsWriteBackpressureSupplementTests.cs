using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsWriteBackpressureSupplementTests
{
    [Theory]
    [InlineData(1, 0, true, true)]
    [InlineData(2, 0, true, false)]
    [InlineData(2, 1, true, true)]
    [InlineData(3, 1, true, false)]
    [InlineData(3, 2, true, true)]
    [InlineData(4, 2, true, false)]
    [InlineData(4, 3, true, true)]
    [InlineData(1, 0, false, false)]
    public async Task ShouldUseMidgesIntegerQueueThresholdOnlyWhenSoftControlsAreEnabled(
        int capacity, int queued, bool enabled, bool slowed)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualSchedulingTimeProvider();
        using var probe = new SubmissionProbe(Failpoint.BeforeRuntimeMetricsResponse);
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false)
            .WithCoordinatorQueueCapacityForTesting(capacity)
            .WithWriteBackpressure(enabled ? new PantsWriteBackpressureConfiguration(L0SoftLimit: 1,
                MaximumAdmissionDelay: TimeSpan.FromMilliseconds(20)) : new());
        await using (var database = await PantsDatabase.OpenForTestingAsync(options,
                         new RuntimeDependencies(probe, runtimeTimeProvider: clock)))
        {
            await using var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("queued"u8.ToArray(), "value"u8.ToArray());
            probe.Arm();
            var barrier = database.Diagnostics.GetRuntimeMetricsAsync().AsTask();
            await probe.Entered.WaitAsync(TestTimeouts.Expected);
            var lookups = Enumerable.Range(0, queued).Select(_ => database.ColumnFamilies.GetAsync("default").AsTask()).ToArray();
            var commit = transaction.CommitAsync(PantsWriteOptions.Sync).AsTask();
            try
            {
                await TestWait.UntilAsync(() => probe.Submissions != 0 || clock.HasTimerDueIn(TimeSpan.FromMilliseconds(1)),
                    "the caller reaches submission or its queue slowdown");
                Assert.Equal(slowed, clock.HasTimerDueIn(TimeSpan.FromMilliseconds(1)));
                Assert.Equal(slowed ? 0 : 1, probe.Submissions);
                if (slowed)
                {
                    clock.Advance(TimeSpan.FromMilliseconds(20));
                    await TestWait.UntilAsync(() => probe.Submissions == 1, "the one delayed submission is registered");
                }
                Assert.False(commit.IsCompleted);
            }
            finally
            {
                probe.Release();
                clock.Advance(TimeSpan.FromMilliseconds(20));
            }
            await barrier.WaitAsync(TestTimeouts.Expected);
            await Task.WhenAll(lookups).WaitAsync(TestTimeouts.Expected);
            await commit.WaitAsync(TestTimeouts.Expected);
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(slowed ? 1 : 0, after.WriteSlowdownCount);
            Assert.Equal(slowed ? TimeSpan.FromMilliseconds(20).Ticks * 100 : 0, after.WriteSlowdownNanosecondsTotal);
            Assert.Equal(1, probe.Submissions);
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertValueAsync(reopened, reopened.ColumnFamilies.DefaultFamily, "queued", "value"u8.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRetainOneSlowdownAndCleanActualSpillRunsOnCommitOrPreSubmissionCancellation(bool canceled)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualSchedulingTimeProvider();
        var submissions = new CountingFailpointHandler(Failpoint.BeforeCommitSubmission);
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false).WithTransactionMemoryPool(2048)
            .WithWriteBackpressure(new PantsWriteBackpressureConfiguration(L0SoftLimit: 1,
                MaximumAdmissionDelay: TimeSpan.FromMilliseconds(20)));
        var expected = new byte[900];
        Array.Fill(expected, (byte)'v');
        await using (var database = await PantsDatabase.OpenForTestingAsync(options,
                         new RuntimeDependencies(submissions, runtimeTimeProvider: clock)))
        {
            await WriteAsync(database, database.ColumnFamilies.DefaultFamily, "seed", "value"u8.ToArray());
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();
            var submittedBefore = submissions.HitCount;
            await using var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            for (var index = 0; index < 6; index++) { transaction.Put(TestBytes.FromString($"spill-{index}"), expected); }
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(directory.Path, "txn"), "*.run"));
            using var cancellation = new CancellationTokenSource();
            var commit = transaction.CommitAsync(PantsWriteOptions.Sync, cancellation.Token).AsTask();
            Assert.True(clock.HasTimerDueIn(TimeSpan.FromMilliseconds(1)));
            Assert.Equal(submittedBefore, submissions.HitCount);
            clock.Advance(TimeSpan.FromMilliseconds(canceled ? 5 : 20));
            if (canceled)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commit.WaitAsync(TestTimeouts.Expected));
            }
            else { await commit.WaitAsync(TestTimeouts.Expected); }
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(before.WriteSlowdownCount + 1, after.WriteSlowdownCount);
            Assert.Equal(submittedBefore + (canceled ? 0 : 1), submissions.HitCount);
            Assert.False(Directory.Exists(Path.Combine(directory.Path, "txn")));
            if (canceled)
            {
                Assert.Equal(before.CurrentSequence, after.CurrentSequence);
                Assert.Equal(before.WalAppendCount, after.WalAppendCount);
                Assert.Equal(before.ManifestNextWalSequence, after.ManifestNextWalSequence);
            }
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        for (var index = 0; index < 6; index++)
        {
            await AssertValueAsync(reopened, reopened.ColumnFamilies.DefaultFamily, $"spill-{index}", canceled ? null : expected);
        }
    }

    [Fact]
    public async Task ShouldSlowEachCallerOnceBeforeCoalescingItsAcceptedWalUnit()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualSchedulingTimeProvider();
        using var probe = new SubmissionProbe(Failpoint.BeforeRuntimeMetricsResponse);
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false)
            .WithWriteBackpressure(new PantsWriteBackpressureConfiguration(L0SoftLimit: 1,
                MaximumAdmissionDelay: TimeSpan.FromMilliseconds(20)));
        await using (var database = await PantsDatabase.OpenForTestingAsync(options,
                         new RuntimeDependencies(probe, runtimeTimeProvider: clock)))
        {
            await WriteAsync(database, database.ColumnFamilies.DefaultFamily, "seed", "value"u8.ToArray());
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            var transactions = new List<IPantsTransaction>();
            for (var index = 0; index < 4; index++)
            {
                var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
                transaction.Put(TestBytes.FromString($"coalesced-{index}"), "value"u8.ToArray());
                transactions.Add(transaction);
            }
            var submittedBefore = probe.Submissions;
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();
            probe.Arm();
            var barrier = database.Diagnostics.GetRuntimeMetricsAsync().AsTask();
            await probe.Entered.WaitAsync(TestTimeouts.Expected);
            var commits = transactions.Select(transaction => transaction.CommitAsync(PantsWriteOptions.Sync).AsTask()).ToArray();
            try
            {
                Assert.Equal(submittedBefore, probe.Submissions);
                Assert.All(commits, task => Assert.False(task.IsCompleted));
                clock.Advance(TimeSpan.FromMilliseconds(20));
                await TestWait.UntilAsync(() => probe.Submissions == submittedBefore + 4, "all four delayed callers queue their mutations");
            }
            finally { probe.Release(); }
            _ = await barrier.WaitAsync(TestTimeouts.Expected);
            await Task.WhenAll(commits).WaitAsync(TestTimeouts.Expected);
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(before.WriteSlowdownCount + 4, after.WriteSlowdownCount);
            Assert.Equal(before.WalAppendCount + 1, after.WalAppendCount);
            Assert.Equal(TimeSpan.FromMilliseconds(80).Ticks * 100,
                after.WriteSlowdownNanosecondsTotal - before.WriteSlowdownNanosecondsTotal);
            foreach (var transaction in transactions) { await transaction.DisposeAsync(); }
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        for (var index = 0; index < 4; index++)
        {
            await AssertValueAsync(reopened, reopened.ColumnFamilies.DefaultFamily, $"coalesced-{index}", "value"u8.ToArray());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldPublishDroppedFamilyAndDebtBeforeWaitingForAuxiliaryCloudMirroring(bool inspectReadAdmission)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualSchedulingTimeProvider();
        using var probe = new SubmissionProbe(Failpoint.BeforeDroppedFamilyAuxiliaryMirror);
        var options = PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "drop-mirror/")
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 4, BackgroundEnabled: false))
            .WithWriteBackpressure(new PantsWriteBackpressureConfiguration(PendingCompactionSoftLimitBytes: 1,
                PendingCompactionHardLimitBytes: 2, MaximumAdmissionDelay: TimeSpan.FromMilliseconds(20)));
        await using (var database = await PantsDatabase.OpenForTestingAsync(options,
                         new RuntimeDependencies(probe, runtimeTimeProvider: clock)))
        {
            var debtor = await database.ColumnFamilies.CreateAsync("debtor");
            for (var index = 0; index < 4; index++)
            {
                await WriteAsync(database, debtor, $"seed-{index}", "value"u8.ToArray(), true);
                await database.Maintenance.FlushAsync(debtor);
            }
            await using var retained = await database.Transactions.BeginAsync(debtor, PantsTransactionMode.ReadOnly);
            await using var contender = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
            contender.Put("after-drop"u8.ToArray(), "value"u8.ToArray());
            var submittedBefore = probe.Submissions;
            var waiter = database.Maintenance.WaitForWriteStallClearAsync(database.ColumnFamilies.DefaultFamily, TestTimeouts.Expected).AsTask();
            Assert.NotNull(await database.ColumnFamilies.GetAsync("debtor"));
            Assert.False(waiter.IsCompleted);
            probe.Arm();
            var drop = database.ColumnFamilies.DropAsync(debtor).AsTask();
            Task? commit = null;
            try
            {
                await probe.Entered.WaitAsync(TestTimeouts.Expected);
                Assert.False(drop.IsCompleted);
                Assert.Equal("value"u8.ToArray(), (await retained.GetAsync("seed-0"u8.ToArray()))?.ToArray());
                if (inspectReadAdmission)
                {
                    IPantsTransaction? unexpected = null;
                    var error = await Record.ExceptionAsync(async () => unexpected = await database.Transactions.BeginAsync(debtor,
                        PantsTransactionMode.ReadOnly));
                    if (unexpected is not null) { await unexpected.DisposeAsync(); }
                    Assert.IsType<PantsInvalidArgumentException>(error);
                }
                else
                {
                    commit = contender.CommitAsync(PantsWriteOptions.CloudStrict).AsTask();
                    Assert.False(clock.HasTimerDueIn(TimeSpan.FromMilliseconds(1)));
                    Assert.Equal(submittedBefore + 1, probe.Submissions);
                    Assert.False(commit.IsCompleted);
                }
            }
            finally
            {
                probe.Release();
                clock.Advance(TimeSpan.FromMilliseconds(20));
                await drop.WaitAsync(TestTimeouts.Expected);
            }
            if (commit is not null) { await commit.WaitAsync(TestTimeouts.Expected); }
            Assert.True(await waiter.WaitAsync(TestTimeouts.Expected));
            Assert.Null(await database.ColumnFamilies.GetAsync("debtor"));
            Assert.Equal("value"u8.ToArray(), (await retained.GetAsync("seed-0"u8.ToArray()))?.ToArray());
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.Null(await reopened.ColumnFamilies.GetAsync("debtor"));
        await AssertValueAsync(reopened, reopened.ColumnFamilies.DefaultFamily, "after-drop", inspectReadAdmission ? null : "value"u8.ToArray());
    }

    static async Task WriteAsync(IPantsDatabase database, IPantsColumnFamily family, string key, byte[] value, bool cloud = false)
    {
        await using var transaction = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), value);
        await transaction.CommitAsync(cloud ? PantsWriteOptions.CloudStrict : PantsWriteOptions.Sync);
    }

    static async Task AssertValueAsync(IPantsDatabase database, IPantsColumnFamily family, string key, byte[]? expected)
    {
        await using var read = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly);
        Assert.Equal(expected, (await read.GetAsync(TestBytes.FromString(key)))?.ToArray());
    }

    sealed class SubmissionProbe(Failpoint held) : IFailpointHandler, IDisposable
    {
        readonly FlushPipelineFailpointHandler _hold = new(held, armOnConstruction: false);
        int _submissions;
        public int Submissions => Volatile.Read(ref _submissions);
        public Task Entered => _hold.WaitUntilEnteredAsync(TestTimeouts.Expected);
        public void Arm() => _hold.Arm();
        public void Release() => _hold.Release();
        public void Dispose() => _hold.Dispose();
        public void Hit(Failpoint failpoint)
        {
            if (failpoint == Failpoint.BeforeCommitSubmission) { Interlocked.Increment(ref _submissions); }
            _hold.Hit(failpoint);
        }
    }
}
