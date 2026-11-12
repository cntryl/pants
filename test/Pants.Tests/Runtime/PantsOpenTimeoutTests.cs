using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsOpenTimeoutTests
{
    [Fact]
    public async Task ShouldAdmitRuntimeWhenLongBudgetTimerRearmRacesItsDisposal()
    {
        using var time = new HeldRearmTimeProvider();
        using var prepared = new StartupBarrier(Failpoint.AfterStartupPrepared);
        var opening = PantsDatabase.OpenForTestingAsync(PantsOpenOptions.InMemory()
            .WithOpenTimeout(TimeSpan.FromDays(100)), new RuntimeDependencies(prepared, runtimeTimeProvider: time)).AsTask();
        var advancing = Task.CompletedTask;
        try
        {
            await prepared.Entered.WaitAsync(TestTimeouts.Expected);
            advancing = Task.Run(() => time.Advance(TimeSpan.FromDays(1)));
            await time.Rearming.WaitAsync(TestTimeouts.Expected);
            prepared.Release();
            await using var database = await opening.WaitAsync(TestTimeouts.Expected);
            await time.Disposed.WaitAsync(TestTimeouts.Expected);
            time.ReleaseRearm();
            await advancing.WaitAsync(TestTimeouts.Expected);
            await WriteAsync(database, 0);
            await AssertValueAsync(database);
        }
        finally
        {
            prepared.Release();
            time.ReleaseRearm();
            _ = await Record.ExceptionAsync(() => advancing);
            await DisposeSuccessfulOpenAsync(opening);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ShouldCancelPreparedOpenWithoutWaitingForItsOwnedCleanup(int mode)
    {
        using var directory = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        using var barrier = new StartupBarrier(Failpoint.AfterStartupPrepared);
        using var cancellation = new CancellationTokenSource();
        var observation = new StartupObservation();
        var options = Options(directory.Path, mode);
        var opening = PantsDatabase.OpenForTestingAsync(options,
            new RuntimeDependencies(barrier, cloudHttpClient: client, startupObserver: observation.Observe),
            cancellation.Token).AsTask();
        try
        {
            await barrier.Entered.WaitAsync(TestTimeouts.Expected);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TestTimeouts.Expected));
            Assert.False(observation.Cleaned.IsCompleted);
        }
        finally
        {
            barrier.Release();
            await observation.Cleaned.WaitAsync(TestTimeouts.Expected);
        }
        Assert.Equal(1, observation.Prepared);
        Assert.Equal(0, observation.Admitted);
        Assert.Equal(1, observation.Aborted);
        await using var reopened = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(cloudHttpClient: client));
        await WriteAsync(reopened, mode);
        await AssertValueAsync(reopened);
    }

    [Fact]
    public async Task ShouldUseOneBudgetAcrossActualMetadataAndServicePreparation()
    {
        using var directory = new TemporaryDirectory();
        var time = new ManualSchedulingTimeProvider();
        var observation = new StartupObservation();
        var opening = PantsDatabase.OpenForTestingAsync(PantsOpenOptions.Local(directory.Path)
            .WithOpenTimeout(TimeSpan.FromSeconds(1)),
            new RuntimeDependencies(runtimeTimeProvider: time, startupObserver: observation.Observe,
                startupPhaseMeasurement: phase =>
                {
                    if (phase.Phase == StartupPhase.Format) { time.Advance(TimeSpan.FromMilliseconds(600)); }
                    if (phase.Phase == StartupPhase.ServiceStartup) { time.Advance(TimeSpan.FromMilliseconds(400)); }
                })).AsTask();
        await Assert.ThrowsAsync<PantsTimeoutException>(() => opening.WaitAsync(TestTimeouts.Expected));
        await observation.Cleaned.WaitAsync(TestTimeouts.Expected);
        Assert.Equal(0, observation.Admitted);
        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        await WriteAsync(reopened, 1);
        await AssertValueAsync(reopened);
    }

    [Fact]
    public async Task ShouldPreserveEarlyCompatibilityFailureAndExactFormatBytesWithTimedOpen()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "FORMAT");
        var marker = "midge-format-version=999\n"u8.ToArray();
        await File.WriteAllBytesAsync(path, marker);
        await Assert.ThrowsAsync<PantsCompatibilityException>(() => PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithOpenTimeout(TimeSpan.FromSeconds(5))).AsTask());
        Assert.Equal(marker, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ShouldReportIndeterminateWhileActualLocalLeaderWriteAwaitsConfirmation()
    {
        using var directory = new TemporaryDirectory();
        using var barrier = new StartupBarrier(Failpoint.AfterLeaseAcquisitionWrite);
        var observation = new StartupObservation();
        var time = new ManualSchedulingTimeProvider();
        var options = PantsOpenOptions.Local(directory.Path).WithOpenTimeout(TimeSpan.FromSeconds(1));
        var opening = PantsDatabase.OpenForTestingAsync(options,
            new RuntimeDependencies(barrier, runtimeTimeProvider: time, startupObserver: observation.Observe)).AsTask();
        try
        {
            await barrier.Entered.WaitAsync(TestTimeouts.Expected);
            Assert.True(File.Exists(Path.Combine(directory.Path, ".midge_leader")));
            time.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<PantsLeaseIndeterminateException>(() => opening.WaitAsync(TestTimeouts.Expected));
            Assert.False(observation.Cleaned.IsCompleted);
            await Assert.ThrowsAsync<PantsLeaseHeldException>(() => PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)).AsTask());
        }
        finally
        {
            barrier.Release();
            await observation.Cleaned.WaitAsync(TestTimeouts.Expected);
        }
        Assert.Contains("1970-01-01T00:00:00Z", await File.ReadAllTextAsync(Path.Combine(directory.Path, ".midge_leader")));
        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        await WriteAsync(reopened, 1);
        await AssertValueAsync(reopened);
    }

    [Fact]
    public async Task ShouldKeepRecoveredFlushDormantUntilActualPreparedRuntimeIsAdmitted()
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(SystemPantsClock.Instance, new RuntimeTelemetry());
        using (var store = LocalDiskStore.Open(directory.Path, state))
        {
            var family = state.FamilyData.Keys.Single();
            using var payload = new CommitPayload(1, PantsTransactionMode.ReadWrite,
                PantsConflictPolicy.LastWriteWins, DateTimeOffset.UtcNow, state.CreateVersion(),
                new TransactionOperationSource(null,
                    [new TransactionIntentOperation(0, CommitOperationKind.Put, family,
                        "key"u8.ToArray(), null, "value"u8.ToArray(), null, null, false)], 1, DateTimeOffset.UtcNow), []);
            store.AppendCommit(payload, state, PantsDurability.Sync);
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        using var barrier = new StartupBarrier(Failpoint.AfterStartupPrepared);
        var observation = new StartupObservation();
        var time = new ManualSchedulingTimeProvider();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        var opening = PantsDatabase.OpenForTestingAsync(options.WithOpenTimeout(TimeSpan.FromSeconds(1)),
            new RuntimeDependencies(barrier, runtimeTimeProvider: time, startupObserver: observation.Observe)).AsTask();
        try
        {
            await barrier.Entered.WaitAsync(TestTimeouts.Expected);
            Assert.Equal(1, observation.Prepared);
            Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
            time.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<PantsTimeoutException>(() => opening.WaitAsync(TestTimeouts.Expected));
        }
        finally
        {
            barrier.Release();
            await observation.Cleaned.WaitAsync(TestTimeouts.Expected);
        }
        Assert.Equal(0, observation.Admitted);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertValueAsync(reopened);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldBoundCallerWaitWhileKnownOwnerIsHeldInLocalStartup(bool simulatedCloud)
    {
        using var directory = new TemporaryDirectory();
        using var barrier = new StartupBarrier(Failpoint.BeforeWalDirectorySync);
        var time = new ManualSchedulingTimeProvider();
        var observation = new StartupObservation();
        var options = Options(directory.Path, simulatedCloud).WithOpenTimeout(TimeSpan.FromSeconds(1));
        var opening = Task.Run(async () => await PantsDatabase.OpenForTestingAsync(options,
            new RuntimeDependencies(barrier, runtimeTimeProvider: time, startupObserver: observation.Observe)));
        try
        {
            await barrier.Entered.WaitAsync(TestTimeouts.Expected);
            time.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<PantsTimeoutException>(() => opening.WaitAsync(TimeSpan.FromSeconds(1)));
            await Assert.ThrowsAsync<PantsLeaseHeldException>(() => PantsDatabase.OpenAsync(
                Options(directory.Path, simulatedCloud)).AsTask());
        }
        finally
        {
            barrier.Release();
            await DisposeSuccessfulOpenAsync(opening);
            await observation.Cleaned.WaitAsync(TestTimeouts.Expected);
        }
        await using var reopened = await PantsDatabase.OpenAsync(Options(directory.Path, simulatedCloud));
        Assert.True(reopened.Cloud?.IsPrimaryLeaseHealthy ?? true);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ShouldRejectLatePreparedRuntimeWhenItsOpenBudgetExpires(int mode)
    {
        using var directory = new TemporaryDirectory();
        using var barrier = new StartupBarrier(Failpoint.AfterStartupPrepared);
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var time = new ManualSchedulingTimeProvider();
        var observation = new StartupObservation();
        var options = Options(directory.Path, mode);
        await using (var seed = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(cloudHttpClient: client)))
        {
            await WriteAsync(seed, mode);
        }
        var opening = Task.Run(async () => await PantsDatabase.OpenForTestingAsync(
            options.WithOpenTimeout(TimeSpan.FromSeconds(1)),
            new RuntimeDependencies(barrier, runtimeTimeProvider: time, startupObserver: observation.Observe,
                cloudHttpClient: client)));
        try
        {
            await barrier.Entered.WaitAsync(TestTimeouts.Expected);
            time.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<PantsTimeoutException>(() => opening.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            barrier.Release();
            await DisposeSuccessfulOpenAsync(opening);
            await observation.Cleaned.WaitAsync(TestTimeouts.Expected);
        }
        Assert.Equal(0, observation.Admitted);
        Assert.Equal(1, observation.Prepared);
        Assert.Equal(1, observation.Aborted);
        if (mode != 0)
        {
            await using var reopened = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(cloudHttpClient: client));
            await AssertValueAsync(reopened);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ShouldLeaveDefaultOpenUnboundedWhenRuntimeResponseBudgetExpires(int mode)
    {
        using var directory = new TemporaryDirectory();
        using var barrier = new StartupBarrier();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var time = new ManualSchedulingTimeProvider();
        var options = Options(directory.Path, mode).WithStorageTimeout(TimeSpan.FromSeconds(1))
            .WithRuntimeResponseTimeout(TimeSpan.FromSeconds(2));
        var opening = Task.Run(async () => await PantsDatabase.OpenForTestingAsync(options,
            new RuntimeDependencies(runtimeTimeProvider: time, cloudHttpClient: client,
                startupPhaseMeasurement: measurement =>
                {
                    if (measurement.Phase == StartupPhase.ServiceStartup) { barrier.Hold(); }
                })));
        try
        {
            await barrier.Entered.WaitAsync(TestTimeouts.Expected);
            time.Advance(TimeSpan.FromSeconds(3));
            Assert.False(opening.IsCompleted);
        }
        finally
        {
            barrier.Release();
        }
        await using var database = await opening.WaitAsync(TestTimeouts.Expected);
        Assert.Null(PantsOpenOptionsResolver.Resolve(options).OpenTimeout);
        await WriteAsync(database, mode);
        await AssertValueAsync(database);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ShouldDisarmStartupBudgetAndCancellationAfterAdmission(int mode)
    {
        using var directory = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var time = new ManualSchedulingTimeProvider();
        var options = Options(directory.Path, mode).WithOpenTimeout(TimeSpan.FromSeconds(1));
        await using (var database = await PantsDatabase.OpenForTestingAsync(options,
            new RuntimeDependencies(runtimeTimeProvider: time, cloudHttpClient: client), cancellation.Token))
        {
            time.Advance(TimeSpan.FromSeconds(2));
            await cancellation.CancelAsync();
            await WriteAsync(database, mode);
            await AssertValueAsync(database);
        }
        if (mode != 0)
        {
            await using var reopened = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(cloudHttpClient: client));
            await AssertValueAsync(reopened);
        }
    }

    [Fact]
    public async Task ShouldReturnTimeoutWithoutRunningBlockingProviderCancellationCallbacksOnCaller()
    {
        using var directory = new TemporaryDirectory();
        using var callback = new StartupBarrier();
        var time = new ManualSchedulingTimeProvider();
        var observation = new StartupObservation();
        var wal = new DisposalTrackingCloudObjectStore();
        var sst = new DisposalTrackingCloudObjectStore();
        var pending = new DelegatingCloudProvider(async token =>
        {
            using var registration = token.Register(callback.Hold);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Cancelled initialization cannot return a store.");
        });
        var topology = new PantsCloudStorageTopology(
            new PantsCloudStorageLocation(new DelegatingCloudProvider(_ => ValueTask.FromResult<IPantsCloudObjectStore>(wal)), "wal"),
            new PantsCloudStorageLocation(new DelegatingCloudProvider(_ => ValueTask.FromResult<IPantsCloudObjectStore>(sst)), "sst"),
            new PantsCloudStorageLocation(pending, "control"));
        var opening = PantsDatabase.OpenForTestingAsync(PantsOpenOptions.CloudMulti(directory.Path, topology)
            .WithOpenTimeout(TimeSpan.FromSeconds(1)),
            new RuntimeDependencies(runtimeTimeProvider: time, startupObserver: observation.Observe)).AsTask();
        try
        {
            await pending.Started.Task.WaitAsync(TestTimeouts.Expected);
            time.Advance(TimeSpan.FromSeconds(1));
            await callback.Entered.WaitAsync(TestTimeouts.Expected);
            await Assert.ThrowsAsync<PantsTimeoutException>(() => opening.WaitAsync(TestTimeouts.Expected));
            Assert.Equal(0, wal.DisposeCount);
        }
        finally
        {
            callback.Release();
            await observation.Cleaned.WaitAsync(TestTimeouts.Expected);
        }
        Assert.Equal(1, wal.DisposeCount);
        Assert.Equal(1, sst.DisposeCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9999)]
    public void ShouldRejectStartupBudgetsBelowOneMillisecond(long ticks) =>
        Assert.Throws<PantsInvalidArgumentException>(() => PantsOpenOptions.InMemory().WithOpenTimeout(TimeSpan.FromTicks(ticks)));

    [Fact]
    public void ShouldKeepOpenBudgetIndependentOfStorageAndRuntimeLimits()
    {
        var options = PantsOpenOptions.InMemory().WithOpenTimeout(TimeSpan.FromMilliseconds(1));
        Assert.Equal(TimeSpan.FromMilliseconds(1), PantsOpenOptionsResolver.Resolve(options).OpenTimeout);
        Assert.Null(options.WithOpenTimeout(null).Runtime.OpenTimeout);
        Assert.Throws<PantsInvalidArgumentException>(() => options.WithOpenTimeout(TimeSpan.MaxValue));
        Assert.Throws<PantsInvalidArgumentException>(() => options.WithOpenTimeout(TimeSpan.MaxValue - TimeSpan.FromTicks(1)));
        Assert.Equal(TimeSpan.FromDays(100), options.WithOpenTimeout(TimeSpan.FromDays(100)).Runtime.OpenTimeout);
    }

    static PantsOpenOptions Options(string path, int mode) => (mode switch
    {
        0 => PantsOpenOptions.InMemory(),
        1 => PantsOpenOptions.Local(path),
        2 => PantsOpenOptions.SimulatedCloud(path, "pants-tests", "timed-open/"),
        _ => PantsOpenOptions.Cloud(path, new PantsCloudStorageLocation(new PantsAzureBlobProvider(
            "account", "container", new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.SasToken("sig=test")), "timed-open"))
    }).WithBackgroundCompaction(false);

    static async Task WriteAsync(IPantsDatabase database, int mode)
    {
        await using var write = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        write.Put("key"u8.ToArray(), "value"u8.ToArray());
        await write.CommitAsync(mode >= 2 ? PantsWriteOptions.CloudStrict : PantsWriteOptions.Sync);
    }

    static async Task AssertValueAsync(IPantsDatabase database)
    {
        await using var read = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("value"u8.ToArray(), (await read.GetAsync("key"u8.ToArray()))?.ToArray());
    }

    static PantsOpenOptions Options(string path, bool simulatedCloud) =>
        (simulatedCloud ? PantsOpenOptions.SimulatedCloud(path, "pants-tests", "timed-open/") :
            PantsOpenOptions.Local(path)).WithBackgroundCompaction(false);

    static async Task DisposeSuccessfulOpenAsync(Task<IPantsDatabase> opening)
    {
        try
        {
            await using var database = await opening.WaitAsync(TestTimeouts.Expected);
        }
        catch (PantsException)
        {
        }
    }

    sealed class StartupObservation
    {
        readonly TaskCompletionSource _cleaned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _admitted;
        int _prepared;
        int _aborted;

        public Task Cleaned => _cleaned.Task;
        public int Admitted => Volatile.Read(ref _admitted);
        public int Prepared => Volatile.Read(ref _prepared);
        public int Aborted => Volatile.Read(ref _aborted);

        public void Observe(StartupEvent startupEvent)
        {
            switch (startupEvent)
            {
                case StartupEvent.Prepared: Interlocked.Increment(ref _prepared); break;
                case StartupEvent.Admitted: Interlocked.Increment(ref _admitted); break;
                case StartupEvent.Aborted: Interlocked.Increment(ref _aborted); break;
                case StartupEvent.CleanupFinished: _cleaned.TrySetResult(); break;
            }
        }
    }

    sealed class HeldRearmTimeProvider : TimeProvider, IDisposable
    {
        readonly ManualSchedulingTimeProvider _inner = new();
        readonly ManualResetEventSlim _release = new();
        readonly TaskCompletionSource _rearming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _timers;

        public Task Rearming => _rearming.Task;
        public Task Disposed => _disposed.Task;
        public override long TimestampFrequency => _inner.TimestampFrequency;
        public override long GetTimestamp() => _inner.GetTimestamp();
        public void Advance(TimeSpan elapsed) => _inner.Advance(elapsed);
        public void ReleaseRearm() => _release.Set();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new HeldTimer(this, _inner.CreateTimer(callback, state, dueTime, period), Interlocked.Increment(ref _timers) == 1);

        public void Dispose() => _release.Dispose();

        sealed class HeldTimer(HeldRearmTimeProvider owner, ITimer inner, bool startupTimer) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (startupTimer)
                {
                    owner._rearming.TrySetResult();
                    if (!owner._release.Wait(TestTimeouts.Expected))
                    {
                        throw new TimeoutException("The startup timer rearm was not released.");
                    }
                }
                return inner.Change(dueTime, period);
            }

            public void Dispose()
            {
                inner.Dispose();
                if (startupTimer) { owner._disposed.TrySetResult(); }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    sealed class StartupBarrier(Failpoint? target = null) : IFailpointHandler, IDisposable
    {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly ManualResetEventSlim _release = new();

        public Task Entered => _entered.Task;

        public void Release() => _release.Set();

        public void Hold()
        {
            _entered.TrySetResult();
            if (!_release.Wait(TestTimeouts.Expected))
            {
                throw new TimeoutException("Startup work was not released.");
            }
        }

        public void Hit(Failpoint failpoint)
        {
            if (target == failpoint)
            {
                Hold();
            }
        }

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
        }
    }
}
