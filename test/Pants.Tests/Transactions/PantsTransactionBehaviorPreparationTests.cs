using Cntryl.Pants.Runtime;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Transactions;

[Collection(RuntimeDiagnosticsTestGroup.Name)]
public sealed class PantsTransactionBehaviorPreparationTests
{
    [Fact]
    public async Task ShouldDistinguishAcceptedLostCommitResponseFromFatalFailureAndRecoverItsValue()
    {
        using var directory = new TemporaryDirectory();
        using var hold = new HeldFirstWalAppend();
        var time = new ManualSchedulingTimeProvider();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false)
            .WithStorageTimeout(TimeSpan.FromMilliseconds(5)).WithRuntimeResponseTimeout(TimeSpan.FromSeconds(1));
        await using (var database = await PantsDatabase.OpenForTestingAsync(options,
                         new RuntimeDependencies(hold, runtimeTimeProvider: time)))
        {
            await using var write = await BeginWriteAsync(database);
            write.Put("accepted"u8.ToArray(), "recoverable"u8.ToArray());
            var commit = write.CommitWithSequenceAsync(PantsWriteOptions.Sync).AsTask();
            await hold.Entered.WaitAsync(TestTimeouts.Expected);
            try
            {
                time.Advance(TimeSpan.FromSeconds(1));
                var failure = await Assert.ThrowsAsync<PantsCommitOutcomeUnknownException>(() => commit.WaitAsync(TestTimeouts.Expected));
                Assert.Equal(PantsErrorSeverity.OutcomeUnknown, failure.Severity);
            }
            finally
            {
                hold.Release();
            }
            await database.ShutdownAsync(TestTimeouts.Expected);
        }
        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.Equal("recoverable"u8.ToArray(), await ReadAsync(reopened, "accepted"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldObserveQueuedTtlAtSerializedPreparationAndPersistOneExpiration(bool spilled)
    {
        using var directory = new TemporaryDirectory();
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeMilliseconds(20_000));
        using var hold = new HeldFirstWalAppend();
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = 0;
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false)
            .WithTransactionMemoryPool(spilled ? 1024 : 64 * 1024).WithTtlClock(clock);
        var value = new byte[900];
        new Random(7).NextBytes(value);
        await using (var database = await PantsDatabase.OpenForTestingAsync(options,
                         new RuntimeDependencies(hold, commitAdmittedObserver: _ =>
                         {
                             if (Interlocked.Increment(ref commits) == 2)
                             {
                                 admitted.TrySetResult();
                             }
                         })))
        {
            await using var first = await BeginWriteAsync(database);
            await using var second = await BeginWriteAsync(database);
            first.Put("first"u8.ToArray(), "stable"u8.ToArray());
            second.Put("a"u8.ToArray(), value, TimeSpan.FromSeconds(1));
            second.Put("b"u8.ToArray(), value, TimeSpan.FromSeconds(1));
            second.Put("control"u8.ToArray(), "stable"u8.ToArray());
            var spillPath = Path.Combine(directory.Path, "txn");
            Assert.Equal(spilled, Directory.Exists(spillPath) && Directory.EnumerateFiles(spillPath, "*.run").Any());
            var firstCommit = first.CommitWithSequenceAsync(PantsWriteOptions.Sync).AsTask();
            await hold.Entered.WaitAsync(TestTimeouts.Expected);
            var secondCommit = second.CommitWithSequenceAsync(PantsWriteOptions.Sync).AsTask();
            try
            {
                await admitted.Task.WaitAsync(TestTimeouts.Expected);
                Assert.False(secondCommit.IsCompleted);
                clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(25_000);
            }
            finally
            {
                hold.Release();
                await Task.WhenAll(firstCommit, secondCommit).WaitAsync(TestTimeouts.Expected);
            }
            Assert.True(await firstCommit < await secondCommit);
            Assert.Equal((await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence, await secondCommit);
            Assert.Equal(value, await ReadAsync(database, "a"));
            Assert.Equal(value, await ReadAsync(database, "b"));
            clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(25_999);
            await using var retained = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
            clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(26_000);
            Assert.Equal(value, (await retained.GetAsync("a"u8.ToArray()))?.ToArray());
            Assert.Null(await ReadAsync(database, "a"));
            Assert.Equal("stable"u8.ToArray(), await ReadAsync(database, "control"));
        }
        var reopenedClock = new ManualClock(DateTimeOffset.FromUnixTimeMilliseconds(25_999));
        await using var reopened = await PantsDatabase.OpenAsync(options.WithTtlClock(reopenedClock));
        Assert.Equal(value, await ReadAsync(reopened, "a"));
        Assert.Equal(value, await ReadAsync(reopened, "b"));
        reopenedClock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(26_000);
        Assert.Null(await ReadAsync(reopened, "a"));
        Assert.Equal("stable"u8.ToArray(), await ReadAsync(reopened, "control"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldKeepThePreparedTtlWhenItsOwnWalCompletionIsDelayed(bool spilled)
    {
        using var directory = new TemporaryDirectory();
        using var hold = new HeldFirstWalAppend();
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeMilliseconds(25_000));
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false)
            .WithTransactionMemoryPool(spilled ? 1024 : 64 * 1024).WithTtlClock(clock);
        var value = new byte[900];
        new Random(17).NextBytes(value);
        await using (var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(hold)))
        {
            await using var write = await BeginWriteAsync(database);
            write.Put("a"u8.ToArray(), value, TimeSpan.FromSeconds(1));
            write.Put("b"u8.ToArray(), value, TimeSpan.FromSeconds(1));
            var commit = write.CommitWithSequenceAsync(PantsWriteOptions.Sync).AsTask();
            await hold.Entered.WaitAsync(TestTimeouts.Expected);
            try
            {
                clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(30_000);
            }
            finally
            {
                hold.Release();
            }
            Assert.True(await commit > 0);
            Assert.Null(await ReadAsync(database, "a"));
            Assert.Null(await ReadAsync(database, "b"));
        }
        var recoveredClock = new ManualClock(DateTimeOffset.FromUnixTimeMilliseconds(25_999));
        await using var reopened = await PantsDatabase.OpenAsync(options.WithTtlClock(recoveredClock));
        Assert.Equal(value, await ReadAsync(reopened, "a"));
        Assert.Equal(value, await ReadAsync(reopened, "b"));
        recoveredClock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(26_000);
        Assert.Null(await ReadAsync(reopened, "a"));
        Assert.Null(await ReadAsync(reopened, "b"));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("local")]
    [InlineData("cloud")]
    public async Task ShouldRejectEqualRangeWithoutChangingAcceptedIntentsOrAllocatingSequence(string mode)
    {
        using var directory = new TemporaryDirectory();
        var options = Options(mode, directory.Path);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await using (var transaction = await BeginWriteAsync(database))
            {
                transaction.Put("prior"u8.ToArray(), "kept"u8.ToArray());
                Assert.Throws<PantsInvalidArgumentException>(() => transaction.DeleteRange(new byte[] { 0, 255 }, new byte[] { 0, 255 }));
                Assert.Equal("kept"u8.ToArray(), (await transaction.GetAsync("prior"u8.ToArray()))?.ToArray());
                await transaction.CommitAsync(WriteOptions(mode));
            }
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();
            await using (var rejectedOnly = await BeginWriteAsync(database))
            {
                Assert.Throws<PantsInvalidArgumentException>(() => rejectedOnly.DeleteRange(ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty));
                await rejectedOnly.CommitAsync(WriteOptions(mode));
            }
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(before.CurrentSequence, after.CurrentSequence);
            Assert.Equal(before.WalAppendCount, after.WalAppendCount);
            Assert.Equal("kept"u8.ToArray(), await ReadAsync(database, "prior"));
        }
        if (mode != "memory")
        {
            await using var reopened = await PantsDatabase.OpenAsync(options);
            Assert.Equal("kept"u8.ToArray(), await ReadAsync(reopened, "prior"));
        }
    }

    [Theory]
    [InlineData("memory", PantsDurability.CloudStrict)]
    [InlineData("local", PantsDurability.CloudAsync)]
    [InlineData("cloud", PantsDurability.Sync)]
    [InlineData("cloud", PantsDurability.Buffered)]
    public async Task ShouldReportInvalidArgumentForIncompatibleDurabilityBeforeAnyEffect(string mode, PantsDurability durability)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(Options(mode, directory.Path));
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();
        foreach (var write in new[] { false, true })
        {
            await using var rejected = await BeginWriteAsync(database);
            if (write)
            {
                rejected.Put("rejected"u8.ToArray(), "absent"u8.ToArray());
            }
            var incompatible = durability switch
            {
                PantsDurability.Sync => PantsWriteOptions.Sync,
                PantsDurability.Buffered => PantsWriteOptions.Buffered,
                PantsDurability.CloudAsync => PantsWriteOptions.CloudAsync,
                PantsDurability.CloudStrict => PantsWriteOptions.CloudStrict,
                _ => throw new ArgumentOutOfRangeException(nameof(durability))
            };
            await Assert.ThrowsAsync<PantsInvalidArgumentException>(() => rejected.CommitAsync(incompatible).AsTask());
        }
        var after = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(before.CurrentSequence, after.CurrentSequence);
        Assert.Equal(before.WalAppendCount, after.WalAppendCount);
        Assert.Equal(0, after.ActiveSnapshots);
        Assert.Null(await ReadAsync(database, "rejected"));
        await using var legal = await BeginWriteAsync(database);
        legal.Put("legal"u8.ToArray(), "value"u8.ToArray());
        await legal.CommitAsync(WriteOptions(mode));
        Assert.Equal("value"u8.ToArray(), await ReadAsync(database, "legal"));
    }

    [Fact]
    public async Task ShouldLeavePendingCloudDurabilityUnchangedForPureEmptyCloudStrict()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(Options("cloud", directory.Path)
            .WithCloudWritePolicy(new PantsCloudWritePolicy(long.MaxValue, long.MaxValue, TimeSpan.FromHours(1), int.MaxValue)));
        await using (var write = await BeginWriteAsync(database))
        {
            write.Put("pending"u8.ToArray(), "visible"u8.ToArray());
            await write.CommitAsync(PantsWriteOptions.CloudAsync);
        }
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(before.WalCloudDurableSequence < before.CurrentSequence);
        await using (var empty = await BeginWriteAsync(database))
        {
            await empty.CommitAsync(PantsWriteOptions.CloudStrict);
        }
        var after = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(before.CurrentSequence, after.CurrentSequence);
        Assert.Equal(before.WalCloudDurableSequence, after.WalCloudDurableSequence);
        Assert.Equal(before.WalAppendCount, after.WalAppendCount);
        Assert.Equal("visible"u8.ToArray(), await ReadAsync(database, "pending"));
    }

    static PantsOpenOptions Options(string mode, string path) => (mode switch
    {
        "memory" => PantsOpenOptions.InMemory(),
        "local" => PantsOpenOptions.Local(path),
        "cloud" => PantsOpenOptions.SimulatedCloud(path, "pants-tests", "transaction-behavior/"),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    }).WithBackgroundCompaction(false);

    static PantsWriteOptions WriteOptions(string mode) => mode == "cloud" ? PantsWriteOptions.CloudStrict : PantsWriteOptions.Sync;

    static ValueTask<IPantsTransaction> BeginWriteAsync(IPantsDatabase database) =>
        database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);

    static async Task<byte[]?> ReadAsync(IPantsDatabase database, string key)
    {
        await using var reader = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        return (await reader.GetAsync(TestBytes.FromString(key)))?.ToArray();
    }

    sealed class HeldFirstWalAppend : IFailpointHandler, IDisposable
    {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly ManualResetEventSlim _release = new();
        int _hits;

        public Task Entered => _entered.Task;

        public void Hit(Failpoint failpoint)
        {
            if (failpoint != Failpoint.BeforeWalAppend || Interlocked.Increment(ref _hits) != 1)
            {
                return;
            }
            _entered.TrySetResult();
            if (!_release.Wait(TestTimeouts.Expected))
            {
                throw new TimeoutException("The first physical WAL append was not released.");
            }
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
        }
    }
}
