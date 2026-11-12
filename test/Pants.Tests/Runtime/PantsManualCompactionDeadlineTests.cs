using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsManualCompactionDeadlineTests
{
    [Fact]
    public async Task ShouldRetainManualStagingReservationUntilTheTimedOutPhysicalWorkerFinishes()
    {
        using var directory = new TemporaryDirectory();
        var seedOptions = PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "manual-deadline/")
            .WithSimulatedCloudLocalStorageBudget(1024 * 1024)
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2, BackgroundEnabled: false));
        await using (var seed = await PantsDatabase.OpenAsync(seedOptions))
        {
            for (var index = 0; index < 3; index++)
            {
                await PutAsync(seed, $"row-{index}", [(byte)index], PantsWriteOptions.CloudStrict);
                await PutAsync(seed, "shared", [(byte)index], PantsWriteOptions.CloudStrict);
                await seed.Maintenance.FlushAsync(seed.ColumnFamilies.DefaultFamily);
            }
        }
        using var held = new HeldMaintenance(Failpoint.AfterCompactionOutputDurable);
        var clock = new ManualTimeProvider();
        var options = seedOptions.WithStorageTimeout(TimeSpan.FromMilliseconds(5))
            .WithRuntimeResponseTimeout(TimeSpan.FromSeconds(1));
        await using (var database = await PantsDatabase.OpenForTestingAsync(options,
                         new RuntimeDependencies(held, runtimeTimeProvider: clock)))
        {
            var compact = database.Maintenance.CompactAllAsync().AsTask();
            try
            {
                await held.WaitUntilBlockedAsync();
                var before = (await database.Diagnostics.GetRuntimeMetricsAsync()).HybridStorageBudget;
                Assert.NotNull(before);
                Assert.True(before.Usage.CompactionStagingReservedBytes > 0);
                clock.Advance(TimeSpan.FromSeconds(1));
                await Assert.ThrowsAsync<PantsIOException>(() => compact.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.False(held.IsReleased);

                var after = (await database.Diagnostics.GetRuntimeMetricsAsync()).HybridStorageBudget;
                Assert.NotNull(after);
                Assert.Equal(before.Usage.CompactionStagingReservedBytes, after.Usage.CompactionStagingReservedBytes);
                await AssertRowsAsync(database, false);
            }
            finally
            {
                held.Release();
                try
                {
                    await compact.WaitAsync(TestTimeouts.Expected);
                }
                catch (PantsException)
                {
                }
            }
            await database.Maintenance.CompactAllAsync();
            Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).HybridStorageBudget!.Usage.CompactionStagingReservedBytes);
            await database.ShutdownAsync(TestTimeouts.Expected);
        }
        await using var reopened = await PantsDatabase.OpenAsync(seedOptions);
        await AssertRowsAsync(reopened, false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldExpireTheOriginalManualDeadlineWhilePriorPhysicalWorkRemainsOwned(bool pendingFlush)
    {
        using var directory = new TemporaryDirectory();
        var seedOptions = Options(directory.Path, false);
        await using (var seed = await PantsDatabase.OpenAsync(seedOptions))
        {
            for (var index = 0; index < 3; index++)
            {
                await PutAsync(seed, $"row-{index}", [(byte)index]);
                await PutAsync(seed, "shared", [(byte)index]);
                await seed.Maintenance.FlushAsync(seed.ColumnFamilies.DefaultFamily);
            }
        }

        using var held = new HeldMaintenance(pendingFlush ? Failpoint.AfterFlushOutputDurable : Failpoint.AfterCompactionOutputDurable);
        var clock = new ManualTimeProvider();
        var options = Options(directory.Path, !pendingFlush)
            .WithStorageTimeout(TimeSpan.FromMilliseconds(5))
            .WithRuntimeResponseTimeout(TimeSpan.FromSeconds(1));
        await using (var database = await PantsDatabase.OpenForTestingAsync(options,
                         new RuntimeDependencies(held, runtimeTimeProvider: clock)))
        {
            var background = Task.CompletedTask;
            if (pendingFlush)
            {
                await PutAsync(database, "pending", [3]);
            }
            else
            {
                background = database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask();
                await held.WaitUntilBlockedAsync();
            }
            var compact = database.Maintenance.CompactAllAsync().AsTask();
            try
            {
                if (pendingFlush)
                {
                    await held.WaitUntilBlockedAsync();
                }
                else
                {
                    await held.WaitForManualAdmissionAsync();
                }
                clock.Advance(TimeSpan.FromSeconds(1));

                await Assert.ThrowsAsync<PantsTimeoutException>(() => compact.WaitAsync(TimeSpan.FromSeconds(2)));
                Assert.False(held.IsReleased);
                await Assert.ThrowsAsync<PantsLeaseHeldException>(() => PantsDatabase.OpenAsync(seedOptions).AsTask());
                await AssertRowsAsync(database, pendingFlush);
            }
            finally
            {
                held.Release();
                try
                {
                    await compact.WaitAsync(TestTimeouts.Expected);
                }
                catch (PantsException)
                {
                }
                try
                {
                    await background.WaitAsync(TestTimeouts.Expected);
                }
                catch (PantsException)
                {
                }
            }
            await database.ShutdownAsync(TestTimeouts.Expected);
        }

        await using var reopened = await PantsDatabase.OpenAsync(seedOptions);
        await AssertRowsAsync(reopened, pendingFlush);
    }

    static PantsOpenOptions Options(string path, bool background) => PantsOpenOptions.Local(path)
        .WithBackgroundCompaction(background)
        .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2, BackgroundEnabled: background));

    static async Task PutAsync(IPantsDatabase database, string key, byte[] value, PantsWriteOptions? options = null)
    {
        await using var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), value);
        await transaction.CommitAsync(options ?? PantsWriteOptions.Sync);
    }

    static async Task AssertRowsAsync(IPantsDatabase database, bool pendingFlush)
    {
        await using var reader = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 3; index++)
        {
            Assert.Equal(new[] { (byte)index }, (await reader.GetAsync(TestBytes.FromString($"row-{index}")))?.ToArray());
        }
        Assert.Equal(pendingFlush ? new byte[] { 3 } : null, (await reader.GetAsync("pending"u8.ToArray()))?.ToArray());
        Assert.Equal(new byte[] { 2 }, (await reader.GetAsync("shared"u8.ToArray()))?.ToArray());
    }

    sealed class HeldMaintenance(Failpoint target) : IFailpointHandler, IDisposable
    {
        readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _manual = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly ManualResetEventSlim _release = new();

        public bool IsReleased => _release.IsSet;

        public void Hit(Failpoint failpoint)
        {
            if (failpoint == Failpoint.BeforeCompactionAdmission)
            {
                _manual.TrySetResult();
            }
            if (failpoint != target)
            {
                return;
            }
            _blocked.TrySetResult();
            if (!_release.Wait(TestTimeouts.Expected))
            {
                throw new TimeoutException("The held physical maintenance operation was not released.");
            }
        }

        public Task WaitUntilBlockedAsync() => _blocked.Task.WaitAsync(TestTimeouts.Expected);

        public Task WaitForManualAdmissionAsync() => _manual.Task.WaitAsync(TestTimeouts.Expected);

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
        }
    }
}
