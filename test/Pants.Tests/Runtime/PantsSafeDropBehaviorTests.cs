using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsSafeDropBehaviorTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ShouldRefuseDropWithoutWaitingForAnOwnedImmutableFlush(
        bool discardUnflushed,
        bool hasActiveData)
    {
        using var directory = new TemporaryDirectory();
        using var held = new HeldFlush();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(2 * 1024 * 1024))
            .WithMemtableLimits(512 * 1024, 128 * 1024)
            .WithTransactionMemoryPool(512 * 1024)
            .WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(held)))
        {
            var family = await database.ColumnFamilies.CreateAsync("orders");
            var frozenValue = new byte[160 * 1024];
            frozenValue[0] = 7;
            await WriteAsync(database, family, "frozen", frozenValue);
            await held.Blocked.WaitAsync(TestTimeouts.Expected);
            if (hasActiveData)
            {
                await WriteAsync(database, family, "active", [9]);
            }

            var before = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(1, before.ImmutableMemtables);
            Assert.Equal(hasActiveData, before.ActiveMemtableBytes > 0);
            var drop = discardUnflushed
                ? database.ColumnFamilies.DropDiscardingUnflushedAsync(family).AsTask()
                : database.ColumnFamilies.DropAsync(family).AsTask();
            try
            {
                var error = await Record.ExceptionAsync(() => drop.WaitAsync(TimeSpan.FromSeconds(2)));
                if (!discardUnflushed && hasActiveData)
                {
                    var unflushed = Assert.IsType<PantsUnflushedDataPresentException>(error);
                    Assert.Equal(family.Id, unflushed.ColumnFamilyId);
                    Assert.Equal(before.ActiveMemtableBytes, unflushed.UnflushedBytes);
                    Assert.True(unflushed.LicensesUnflushedDiscard);
                }
                else
                {
                    Assert.False(Assert.IsType<PantsBusyException>(error).LicensesUnflushedDiscard);
                }

                Assert.False(held.IsReleased);
                var after = await database.Diagnostics.GetRuntimeMetricsAsync();
                Assert.Equal(before.CurrentSequence, after.CurrentSequence);
                Assert.Equal(before.ImmutableMemtables, after.ImmutableMemtables);
                Assert.Equal(before.ImmutableMemtableBytes, after.ImmutableMemtableBytes);
                Assert.Equal(family.Id, (await database.ColumnFamilies.GetAsync("orders"))!.Id);
                await using var read = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly);
                Assert.Equal(frozenValue, (await read.GetAsync("frozen"u8.ToArray()))?.ToArray());
                Assert.Equal(hasActiveData ? new byte[] { 9 } : null,
                    (await read.GetAsync("active"u8.ToArray()))?.ToArray());
            }
            finally
            {
                held.Release();
                try
                {
                    await drop.WaitAsync(TestTimeouts.Expected);
                }
                catch (PantsException)
                {
                }
            }

            await database.Maintenance.FlushAsync(family);
            await database.ColumnFamilies.DropAsync(family);
            Assert.Null(await database.ColumnFamilies.GetAsync("orders"));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.Null(await reopened.ColumnFamilies.GetAsync("orders"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ShouldIdentifyTheUnflushedFamilyBeforePermittingExplicitDiscard(int mode)
    {
        using var directory = new TemporaryDirectory();
        var options = mode switch
        {
            0 => PantsOpenOptions.InMemory(),
            1 => PantsOpenOptions.Local(directory.Path),
            _ => PantsOpenOptions.SimulatedCloud(directory.Path, "safe-drop", "unflushed")
        };
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = await database.ColumnFamilies.CreateAsync("orders");
            await using (var write = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite))
            {
                write.Put("retained"u8.ToArray(), "value"u8.ToArray());
                await write.CommitAsync(PantsWriteOptions.BestEffort);
            }

            var before = await database.Diagnostics.GetRuntimeMetricsAsync();

            var error = await Assert.ThrowsAsync<PantsUnflushedDataPresentException>(() =>
                database.ColumnFamilies.DropAsync(family).AsTask());

            Assert.Equal(PantsErrorCode.UnflushedDataPresent, error.Code);
            Assert.Equal(PantsErrorSeverity.Caller, error.Severity);
            Assert.True(error.LicensesUnflushedDiscard);
            Assert.Equal(family.Id, error.ColumnFamilyId);
            Assert.Equal(before.ActiveMemtableBytes, error.UnflushedBytes);
            Assert.Equal(before.CurrentSequence, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
            Assert.Equal(family.Id, (await database.ColumnFamilies.GetAsync("orders"))!.Id);
            await using (var read = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly))
            {
                Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                    await read.GetAsync("retained"u8.ToArray()))));
            }

            await database.ColumnFamilies.DropDiscardingUnflushedAsync(family);
            Assert.Null(await database.ColumnFamilies.GetAsync("orders"));
        }

        if (mode != 0)
        {
            await using var reopened = await PantsDatabase.OpenAsync(options);
            Assert.Null(await reopened.ColumnFamilies.GetAsync("orders"));
        }
    }

    static async Task WriteAsync(IPantsDatabase database, IPantsColumnFamily family, string key, byte[] value)
    {
        await using var write = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite);
        write.Put(TestBytes.FromString(key), value);
        await write.CommitAsync(PantsWriteOptions.Sync);
    }

    sealed class HeldFlush : IFailpointHandler, IDisposable
    {
        readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly ManualResetEventSlim _release = new();
        int _hit;

        public Task Blocked => _blocked.Task;

        public bool IsReleased => _release.IsSet;

        public void Hit(Failpoint failpoint)
        {
            if (failpoint != Failpoint.BeforeFlushPublication || Interlocked.Exchange(ref _hit, 1) != 0)
            {
                return;
            }

            _blocked.TrySetResult();
            if (!_release.Wait(TestTimeouts.Expected))
            {
                throw new TimeoutException("The owned flush was not released.");
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
