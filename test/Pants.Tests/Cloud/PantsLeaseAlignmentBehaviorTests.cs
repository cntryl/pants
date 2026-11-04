using System.Net;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class PantsLeaseAlignmentBehaviorTests
{
    [Theory]
    [InlineData(10, 5)]
    [InlineData(60, 30)]
    public async Task ShouldDeriveUnspecifiedSkewFromTheLatestTtlAndPreserveExplicitOverrides(int ttl, int skew)
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithLeaseTimeToLive(TimeSpan.FromSeconds(ttl));
        Assert.Equal(TimeSpan.FromSeconds(skew), options.Lease.ClockSkewTolerance);
        Assert.Equal(TimeSpan.FromSeconds(5), options.WithLeaseTimeToLive(TimeSpan.FromSeconds(10)).Lease.ClockSkewTolerance);
        var explicitOptions = options.WithLeaseClockSkewTolerance(TimeSpan.FromSeconds(2))
            .WithLeaseTimeToLive(TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.FromSeconds(2), explicitOptions.Lease.ClockSkewTolerance);
        Assert.Equal(TimeSpan.FromSeconds(7), PantsOpenOptions.Create(new PantsStorageConfiguration.Local(directory.Path),
                lease: new PantsLeaseConfiguration(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(7)))
            .WithLeaseTimeToLive(TimeSpan.FromSeconds(10)).Lease.ClockSkewTolerance);
        await using var database = await PantsDatabase.OpenAsync(options);
        await WriteAsync(database, "derived", PantsWriteOptions.Sync);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectCloudTtlAtTheRenewalMarginBeforeCreatingStorage(bool simulated)
    {
        using var directory = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(async () =>
        {
            var options = (simulated ? PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "ttl-boundary/")
                : PantsOpenOptions.Cloud(directory.Path, Location()))
                .WithLeaseTimeToLive(TimeSpan.FromMilliseconds(16500));
            await using var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(cloudHttpClient: client));
        });
        Assert.False(File.Exists(Path.Combine(directory.Path, "FORMAT")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRetryTransientRenewalReadWhileOldValidityRemainsAndKeepWriting(bool rawIo)
    {
        using var directory = new TemporaryDirectory();
        using var storage = new InMemoryAzureBlobHandler();
        using var probe = new LeaseProbeHandler(storage);
        using var client = new HttpClient(probe);
        var monotonic = new ManualSchedulingTimeProvider();
        var wall = new ManualClock(DateTimeOffset.UnixEpoch);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = PantsOpenOptions.Cloud(directory.Path, Location()).WithBackgroundCompaction(false)
            .WithLeaseLossCallback(() => lost.TrySetResult());
        await using var database = await PantsDatabase.OpenForTestingAsync(options,
            new RuntimeDependencies(cloudHttpClient: client, runtimeTimeProvider: monotonic, leaseClock: wall));
        await WriteAsync(database, "before");
        probe.FailNextLeaseRead = true;
        probe.RawIoReadFailure = rawIo;
        Advance(monotonic, wall, TimeSpan.FromSeconds(10));
        await probe.ReadFailed.Task.WaitAsync(TestTimeouts.Expected);
        await WaitUntilAsync(() => lost.Task.IsCompleted || monotonic.HasTimerDueIn(TimeSpan.FromSeconds(1)));

        Assert.True(database.PersistentStorage!.IsPrimaryLeaseHealthy);
        Assert.False(lost.Task.IsCompleted);
        Advance(monotonic, wall, TimeSpan.FromSeconds(1));
        await probe.PutSucceeded.Task.WaitAsync(TestTimeouts.Expected);
        Assert.True(database.PersistentStorage.IsPrimaryLeaseHealthy);
        await WriteAsync(database, "after");
        Assert.False(lost.Task.IsCompleted);
    }

    [Fact]
    public async Task ShouldRereadOwnershipAfterDefiniteRenewalCasRejection()
    {
        using var directory = new TemporaryDirectory();
        using var storage = new InMemoryAzureBlobHandler();
        using var probe = new LeaseProbeHandler(storage);
        using var client = new HttpClient(probe);
        var monotonic = new ManualSchedulingTimeProvider();
        var wall = new ManualClock(DateTimeOffset.UnixEpoch);
        var losses = 0;
        var options = PantsOpenOptions.Cloud(directory.Path, Location()).WithBackgroundCompaction(false)
            .WithLeaseLossCallback(() => Interlocked.Increment(ref losses));
        await using var database = await PantsDatabase.OpenForTestingAsync(options,
            new RuntimeDependencies(cloudHttpClient: client, runtimeTimeProvider: monotonic, leaseClock: wall));
        probe.RejectNextLeasePut = true;
        Advance(monotonic, wall, TimeSpan.FromSeconds(10));
        await probe.PutRejected.Task.WaitAsync(TestTimeouts.Expected);
        await WaitUntilAsync(() => Volatile.Read(ref losses) != 0 || probe.PutSucceeded.Task.IsCompleted);

        Assert.True(database.PersistentStorage!.IsPrimaryLeaseHealthy);
        Assert.True(probe.ReadsAfterRejection > 0);
        Assert.Equal(0, Volatile.Read(ref losses));
        await WriteAsync(database, "retry");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldFenceAnAmbiguousRenewalAndExpireOnlyItsExactCandidate(bool deferred)
    {
        using var directory = new TemporaryDirectory();
        using var storage = new InMemoryAzureBlobHandler();
        using var probe = new LeaseProbeHandler(storage);
        using var client = new HttpClient(probe);
        var monotonic = new ManualSchedulingTimeProvider();
        var wall = new ManualClock(DateTimeOffset.UnixEpoch);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = PantsOpenOptions.Cloud(directory.Path, Location()).WithBackgroundCompaction(false)
            .WithLeaseLossCallback(() => lost.TrySetResult());
        await using var database = await PantsDatabase.OpenForTestingAsync(options,
            new RuntimeDependencies(cloudHttpClient: client, runtimeTimeProvider: monotonic, leaseClock: wall));
        await WriteAsync(database, "before");
        probe.FailNextLeasePutAfterPersisting = !deferred;
        probe.DeferNextLeasePutUntilReadback = deferred;
        Advance(monotonic, wall, TimeSpan.FromSeconds(10));
        await probe.AmbiguousPut.Task.WaitAsync(TestTimeouts.Expected);

        await lost.Task.WaitAsync(TestTimeouts.Expected);
        if (deferred)
        {
            await probe.LatePutApplied.Task.WaitAsync(TestTimeouts.Expected);
            await WaitUntilAsync(() => monotonic.HasTimerDueIn(TimeSpan.FromMilliseconds(50)));
            Advance(monotonic, wall, TimeSpan.FromMilliseconds(50));
        }

        Assert.False(database.PersistentStorage!.IsPrimaryLeaseHealthy);
        await Assert.ThrowsAsync<PantsFencedException>(() => WriteAsync(database, "refused"));
        await probe.ExpiredPut.Task.WaitAsync(TestTimeouts.Expected);
        await using var leaseObjects = new AzureBlobObjectStore((PantsAzureBlobProvider)Location().Provider,
            "alignment/", client, TimeSpan.FromSeconds(30));
        var record = await new CloudObjectLeaseStore(leaseObjects, PantsCloudObjectLayout.LeaseObjectKey)
            .ReadAsync(CancellationToken.None);
        Assert.NotNull(record);
        Assert.True(record.Lease.ExpiresAtUtc + options.Lease.ClockSkewTolerance < wall.UtcNow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldAcceptCloudTtlOneTickAboveTheRenewalMargin(bool simulated)
    {
        using var directory = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var ttl = TimeSpan.FromMilliseconds(16500) + TimeSpan.FromTicks(1);
        var options = (simulated ? PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "ttl-valid/")
            : PantsOpenOptions.Cloud(directory.Path, Location())).WithLeaseTimeToLive(ttl);
        await using var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(cloudHttpClient: client));
        await WriteAsync(database, "valid-boundary");
    }

    [Fact]
    public async Task ShouldScheduleRenewalFromAdvancedValidityInsteadOfAFixedClampedPeriod()
    {
        using var directory = new TemporaryDirectory();
        using var storage = new InMemoryAzureBlobHandler();
        using var probe = new LeaseProbeHandler(storage);
        using var client = new HttpClient(probe);
        var monotonic = new ManualSchedulingTimeProvider();
        var wall = new ManualClock(DateTimeOffset.UnixEpoch);
        var options = PantsOpenOptions.Cloud(directory.Path, Location()).WithLeaseTimeToLive(TimeSpan.FromSeconds(60));
        await using var database = await PantsDatabase.OpenForTestingAsync(options,
            new RuntimeDependencies(cloudHttpClient: client, runtimeTimeProvider: monotonic, leaseClock: wall));
        Assert.True(monotonic.HasTimerDueIn(TimeSpan.FromSeconds(20)));
        probe.BeforeNextLeaseRead = _ =>
        {
            Advance(monotonic, wall, TimeSpan.FromSeconds(4));
            return Task.CompletedTask;
        };

        Advance(monotonic, wall, TimeSpan.FromSeconds(20));
        await probe.PutSucceeded.Task.WaitAsync(TestTimeouts.Expected);
        await WaitUntilAsync(() => monotonic.HasTimerDueIn(TimeSpan.FromSeconds(20)));

        Assert.Equal(TimeSpan.FromSeconds(24).Ticks, monotonic.GetTimestamp());
        Assert.True(database.PersistentStorage!.IsPrimaryLeaseHealthy);
    }

    [Theory]
    [InlineData(30, 4500)]
    [InlineData(2, 2000)]
    public async Task ShouldCapRenewalGetAtHalfTheRemainingWindowThenRetryWithoutLoss(int providerSeconds, int readMilliseconds)
    {
        using var directory = new TemporaryDirectory();
        using var storage = new InMemoryAzureBlobHandler();
        using var probe = new LeaseProbeHandler(storage);
        using var client = new HttpClient(probe);
        var monotonic = new ManualSchedulingTimeProvider();
        var wall = new ManualClock(DateTimeOffset.UnixEpoch);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var losses = 0;
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(directory.Path, Location()).WithStorageTimeout(TimeSpan.FromSeconds(providerSeconds)).WithLeaseLossCallback(() => Interlocked.Increment(ref losses)),
            new RuntimeDependencies(cloudHttpClient: client, runtimeTimeProvider: monotonic, leaseClock: wall));
        probe.BeforeNextLeaseRead = async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { canceled.TrySetResult(); }
        };
        Advance(monotonic, wall, TimeSpan.FromSeconds(10));
        await entered.Task.WaitAsync(TestTimeouts.Expected);

        Advance(monotonic, wall, TimeSpan.FromMilliseconds(readMilliseconds));
        await canceled.Task.WaitAsync(TestTimeouts.Expected);
        await WaitUntilAsync(() => monotonic.HasTimerDueIn(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, Volatile.Read(ref losses));
        Assert.True(database.PersistentStorage!.IsPrimaryLeaseHealthy);
        Advance(monotonic, wall, TimeSpan.FromSeconds(1));
        await probe.PutSucceeded.Task.WaitAsync(TestTimeouts.Expected);
        await WriteAsync(database, "after-get-timeout");
        Assert.Equal(0, Volatile.Read(ref losses));
    }

    [Fact]
    public async Task ShouldSpendOneOldValidityWindowAcrossTheRenewalReadAndConditionalWrite()
    {
        using var directory = new TemporaryDirectory();
        using var storage = new InMemoryAzureBlobHandler();
        using var probe = new LeaseProbeHandler(storage);
        using var client = new HttpClient(probe);
        var monotonic = new ManualSchedulingTimeProvider();
        var wall = new ManualClock(DateTimeOffset.UnixEpoch);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(directory.Path, Location()).WithLeaseLossCallback(() => lost.TrySetResult()),
            new RuntimeDependencies(cloudHttpClient: client, runtimeTimeProvider: monotonic, leaseClock: wall));
        probe.BeforeNextLeaseRead = _ =>
        {
            Advance(monotonic, wall, TimeSpan.FromSeconds(4));
            return Task.CompletedTask;
        };
        probe.BeforeNextLeasePut = async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { canceled.TrySetResult(); }
        };
        Advance(monotonic, wall, TimeSpan.FromSeconds(10));
        await entered.Task.WaitAsync(TestTimeouts.Expected);
        Assert.Equal(TimeSpan.FromSeconds(14).Ticks, monotonic.GetTimestamp());

        Advance(monotonic, wall, TimeSpan.FromSeconds(5));
        await canceled.Task.WaitAsync(TestTimeouts.Expected);
        await lost.Task.WaitAsync(TestTimeouts.Expected);

        Assert.Equal(TimeSpan.FromSeconds(19).Ticks, monotonic.GetTimestamp());
        Assert.False(database.PersistentStorage!.IsPrimaryLeaseHealthy);
        await Assert.ThrowsAsync<PantsFencedException>(() => WriteAsync(database, "window-refused"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ShouldAcquireNormallyOnlyAfterTtlAndSkewAndRecoverTheAcknowledgedRow(int backend)
    {
        using var directory = new TemporaryDirectory();
        using var successorDirectory = new TemporaryDirectory();
        using var storage = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(storage);
        var time = new ManualSchedulingTimeProvider();
        var wall = new ManualClock(DateTimeOffset.UnixEpoch);
        var losses = 0;
        var options = backend switch
        {
            0 => PantsOpenOptions.Local(directory.Path),
            1 => PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "normal-acquisition/"),
            _ => PantsOpenOptions.Cloud(directory.Path, Location())
        };
        options = options.WithBackgroundCompaction(false).WithLeaseLossCallback(() => Interlocked.Increment(ref losses));
        var dependencies = new RuntimeDependencies(cloudHttpClient: client, leaseClock: wall,
            runtimeTimeProvider: time, leaseHeartbeatInterval: TimeSpan.FromHours(1));
        await using (var first = await PantsDatabase.OpenForTestingAsync(options, dependencies))
        {
            await WriteAsync(first, "acknowledged", backend == 0 ? PantsWriteOptions.Sync : PantsWriteOptions.CloudStrict);
            wall.UtcNow = DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(40);
            if (backend == 0)
            {
                var before = await File.ReadAllBytesAsync(Path.Combine(directory.Path, ".midge_leader"));
                Assert.Throws<PantsLeaseHeldException>(() => FileLease.Acquire(directory.Path, 0, TimeSpan.FromSeconds(15),
                    null, TimeSpan.FromHours(1), wall, TimeSpan.FromSeconds(30), time));
                Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(directory.Path, ".midge_leader")));
                wall.UtcNow += TimeSpan.FromSeconds(6);
                using var successor = FileLease.Acquire(directory.Path, 0, TimeSpan.FromSeconds(15),
                    null, TimeSpan.FromHours(1), wall, TimeSpan.FromSeconds(30), time);
                Assert.Equal(2UL, successor.Epoch);
                await Assert.ThrowsAsync<PantsFencedException>(() => WriteAsync(first, "refused", PantsWriteOptions.Sync));
            }
            else if (backend == 1)
            {
                var leaseStore = new CloudObjectLeaseStore(new SimulatedCloudObjectStore(Path.Combine(directory.Path, "cloud_store")),
                    PantsCloudObjectLayout.LeaseObjectKey);
                var before = await leaseStore.ReadAsync(CancellationToken.None);
                using var successor = new CloudLeaseCoordinator(leaseStore, wall, "normal-successor", TimeSpan.FromSeconds(30),
                    TimeSpan.FromSeconds(15), timeProvider: time);
                await Assert.ThrowsAsync<PantsLeaseHeldException>(() => successor.AcquireAsync(CancellationToken.None).AsTask());
                Assert.Equal(before, await leaseStore.ReadAsync(CancellationToken.None));
                wall.UtcNow += TimeSpan.FromSeconds(6);
                Assert.Equal(2UL, await successor.AcquireAsync(CancellationToken.None));
                await Assert.ThrowsAsync<PantsFencedException>(() => WriteAsync(first, "refused"));
                await successor.ReleaseAsync(CancellationToken.None);
            }
            else
            {
                var nextOptions = PantsOpenOptions.Cloud(successorDirectory.Path, Location()).WithBackgroundCompaction(false);
                var before = storage.GetObjectText("midge_primary_lease.json");
                await Assert.ThrowsAsync<PantsLeaseHeldException>(() => PantsDatabase.OpenForTestingAsync(nextOptions, dependencies).AsTask());
                Assert.Equal(before, storage.GetObjectText("midge_primary_lease.json"));
                wall.UtcNow += TimeSpan.FromSeconds(6);
                await using var successor = await PantsDatabase.OpenForTestingAsync(nextOptions, dependencies);
                await Assert.ThrowsAsync<PantsFencedException>(() => WriteAsync(first, "refused"));
                await WriteAsync(successor, "successor-write");
            }
            Assert.Equal(1, Volatile.Read(ref losses));
        }
        if (backend == 0)
        {
            // Local release is best effort while the fenced predecessor is releasing its mutation guard.
            // Recover after a normal stale-record boundary even if that release retained the record.
            wall.UtcNow += TimeSpan.FromSeconds(46);
        }
        await using var reopened = await PantsDatabase.OpenForTestingAsync(options, dependencies);
        await using var read = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("value"u8.ToArray(), (await read.GetAsync("acknowledged"u8.ToArray()))?.ToArray());
        Assert.Null(await read.GetAsync("refused"u8.ToArray()));
        if (backend == 2)
        {
            Assert.Equal("value"u8.ToArray(), (await read.GetAsync("successor-write"u8.ToArray()))?.ToArray());
        }
    }

    [Fact]
    public async Task ShouldFenceAtTheProviderWriteCapWhileALateCallbackStillOwnsItsPhysicalEffect()
    {
        using var cache = new TemporaryDirectory();
        using var remote = new TemporaryDirectory();
        var store = new DelayedRenewalEffectStore(remote.Path);
        var time = new ManualSchedulingTimeProvider();
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var location = new PantsCloudStorageLocation(new DelegatingCloudProvider(
            _ => ValueTask.FromResult<IPantsCloudObjectStore>(store)), "late-renewal");
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Cloud(cache.Path, location).WithStorageTimeout(TimeSpan.FromSeconds(2))
                .WithLeaseLossCallback(() =>
                {
                    lost.TrySetResult();
                    throw new InvalidOperationException("Injected user loss callback failure after notification.");
                }),
            new RuntimeDependencies(runtimeTimeProvider: time, leaseClock: clock));
        store.Arm();
        Advance(time, clock, TimeSpan.FromSeconds(10));
        await store.Applied.Task.WaitAsync(TestTimeouts.Expected);
        try
        {
            Advance(time, clock, TimeSpan.FromSeconds(3));

            Assert.False(database.PersistentStorage!.IsPrimaryLeaseHealthy);
            Assert.True(lost.Task.IsCompleted);
            Assert.False(store.Expired.Task.IsCompleted);
            await Assert.ThrowsAsync<PantsFencedException>(() => WriteAsync(database, "late-refused"));
        }
        finally
        {
            store.Release();
        }
        await store.Expired.Task.WaitAsync(TestTimeouts.Expected);
        var record = await new CloudObjectLeaseStore(store, PantsCloudObjectLayout.LeaseObjectKey).ReadAsync(CancellationToken.None);
        Assert.NotNull(record);
        Assert.True(record.Lease.ExpiresAtUtc + database.Options.Lease.ClockSkewTolerance < clock.UtcNow);
    }

    static PantsCloudStorageLocation Location() => new(new PantsAzureBlobProvider("account", "container", null,
        new PantsAzureCredentialSource.SharedKey(Convert.ToBase64String(new byte[32]))), "alignment/");

    static void Advance(ManualSchedulingTimeProvider time, ManualClock wall, TimeSpan elapsed)
    {
        wall.UtcNow += elapsed;
        time.Advance(elapsed);
    }

    static async Task WaitUntilAsync(Func<bool> predicate)
    {
        async Task WaitAsync()
        {
            while (!predicate())
            {
                await Task.Yield();
            }
        }
        await WaitAsync().WaitAsync(TestTimeouts.Expected);
    }

    static async Task WriteAsync(IPantsDatabase database, string key, PantsWriteOptions? options = null)
    {
        await using var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), "value"u8.ToArray());
        await transaction.CommitAsync(options ?? PantsWriteOptions.CloudStrict);
    }

    sealed class DelayedRenewalEffectStore(string root) : ICloudObjectStore
    {
        readonly SimulatedCloudObjectStore _inner = new(root);
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _armed;
        public TaskCompletionSource Applied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Expired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Volatile.Write(ref _armed, 1);
        public void Release() => _release.TrySetResult();
        public ValueTask<CloudObject?> GetAsync(string key, CancellationToken token) => _inner.GetAsync(key, token);
        public ValueTask<CloudObject?> GetRangeAsync(string key, ulong offset, int length, CancellationToken token) =>
            _inner.GetRangeAsync(key, offset, length, token);
        public ValueTask<CloudObjectMetadata?> HeadAsync(string key, CancellationToken token) => _inner.HeadAsync(key, token);
        public ValueTask<CloudObjectListPage> ListPageAsync(string prefix, string? continuation, CancellationToken token) =>
            _inner.ListPageAsync(prefix, continuation, token);
        public ValueTask<CloudObjectDeleteOutcome> DeleteAsync(string key, CloudObjectDeleteCondition condition, CancellationToken token) =>
            _inner.DeleteAsync(key, condition, token);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public async ValueTask<bool> PutAsync(string key, ReadOnlyMemory<byte> data, CloudObjectWriteCondition condition, CancellationToken token)
        {
            var applied = await _inner.PutAsync(key, data, condition, token);
            if (key == PantsCloudObjectLayout.LeaseObjectKey && condition is CloudObjectWriteCondition.IfVersion && applied)
            {
                if (Interlocked.Exchange(ref _armed, 0) != 0)
                {
                    Applied.TrySetResult();
                    await _release.Task;
                }
                else if (Applied.Task.IsCompleted)
                {
                    Expired.TrySetResult();
                }
            }
            return applied;
        }
    }

    sealed class LeaseProbeHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public Func<CancellationToken, Task>? BeforeNextLeaseRead { get; set; }
        public Func<CancellationToken, Task>? BeforeNextLeasePut { get; set; }
        public bool DeferNextLeasePutUntilReadback { get; set; }
        HttpRequestMessage? _deferredPut;
        public TaskCompletionSource LatePutApplied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailNextLeaseRead { get; set; }
        public bool RawIoReadFailure { get; set; }
        public bool RejectNextLeasePut { get; set; }
        public bool FailNextLeasePutAfterPersisting { get; set; }
        public int ReadsAfterRejection;
        public TaskCompletionSource ReadFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PutRejected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PutSucceeded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AmbiguousPut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ExpiredPut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("midge_primary_lease.json", StringComparison.Ordinal))
            {
                return await base.SendAsync(request, cancellationToken);
            }
            if (request.Method == HttpMethod.Get)
            {
                var before = BeforeNextLeaseRead;
                BeforeNextLeaseRead = null;
                if (before is not null) { await before(cancellationToken); }
                if (PutRejected.Task.IsCompleted)
                {
                    Interlocked.Increment(ref ReadsAfterRejection);
                }
                if (FailNextLeaseRead)
                {
                    FailNextLeaseRead = false;
                    ReadFailed.TrySetResult();
                    if (RawIoReadFailure) { throw new IOException("Injected raw provider lease GET callback error."); }
                    throw new PantsIOException("Injected transient lease GET callback failure.");
                }
            }
            if (request.Method == HttpMethod.Put && BeforeNextLeasePut is { } beforePut)
            {
                BeforeNextLeasePut = null;
                await beforePut(cancellationToken);
            }
            if (request.Method == HttpMethod.Put && DeferNextLeasePutUntilReadback)
            {
                DeferNextLeasePutUntilReadback = false;
                _deferredPut = new HttpRequestMessage(request.Method, request.RequestUri);
                foreach (var header in request.Headers) { _deferredPut.Headers.TryAddWithoutValidation(header.Key, header.Value); }
                _deferredPut.Content = new ByteArrayContent(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
                AmbiguousPut.TrySetResult();
                throw new PantsIOException("Injected lost response before a delayed conditional renewal effect.");
            }
            if (request.Method == HttpMethod.Put && RejectNextLeasePut)
            {
                RejectNextLeasePut = false;
                PutRejected.TrySetResult();
                return ProviderPreconditionResponses.Azure();
            }
            var response = await base.SendAsync(request, cancellationToken);
            if (request.Method == HttpMethod.Get && _deferredPut is { } deferredPut)
            {
                _deferredPut = null;
                using (deferredPut)
                using (var applied = await base.SendAsync(deferredPut, cancellationToken))
                {
                    Assert.True(applied.IsSuccessStatusCode);
                }
                LatePutApplied.TrySetResult();
            }
            if (request.Method == HttpMethod.Put && request.Headers.IfMatch.Count != 0)
            {
                if (FailNextLeasePutAfterPersisting)
                {
                    FailNextLeasePutAfterPersisting = false;
                    response.Dispose();
                    AmbiguousPut.TrySetResult();
                    throw new PantsIOException("Injected lost renewal response after conditional persistence.");
                }
                if (AmbiguousPut.Task.IsCompleted)
                {
                    ExpiredPut.TrySetResult();
                }
                PutSucceeded.TrySetResult();
            }
            return response;
        }
    }
}
