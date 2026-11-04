using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class PantsCloudDurabilityWaitTests
{
    [Fact]
    public async Task ShouldTreatZeroTimeoutAsObservationAndRejectANegativeBudgetWithoutPublication()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await PantsDatabase.OpenForTestingAsync(Options(cache.Path),
            new RuntimeDependencies(cloudHttpClient: client));
        var covered = await database.Cloud!.GetDurableSequenceAsync();
        Assert.True(await database.Cloud.WaitForDurableAsync(covered, TimeSpan.Zero));
        var target = await CommitAsync(database, "key", "pending");
        Assert.False(await database.Cloud.WaitForDurableAsync(target, TimeSpan.Zero));
        await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
            database.Cloud.WaitForDurableAsync(target, TimeSpan.FromTicks(-1)).AsTask());
        Assert.Equal(covered, await database.Cloud.GetDurableSequenceAsync());
        Assert.Equal("pending"u8.ToArray(), await ReadAsync(database, "key"));
    }

    [Fact]
    public async Task ShouldConsumeQueueTimeWithoutRenewingTheConfirmationBudgetOrSealingAfterExpiry()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        using var hold = new ArmedFailpointHold(Failpoint.BeforeWalAppend);
        var time = new ManualSchedulingTimeProvider();
        await using var database = await PantsDatabase.OpenForTestingAsync(Options(cache.Path),
            new RuntimeDependencies(hold, cloudHttpClient: client, runtimeTimeProvider: time));
        var target = await CommitAsync(database, "target", "pending");
        var before = await database.Cloud!.GetDurableSequenceAsync();
        hold.Arm();
        var blocking = CommitAsync(database, "blocker", "later");
        await hold.Entered.WaitAsync(TestTimeouts.Expected);
        try
        {
            var runtime = Assert.IsType<DatabaseInstance>(database);
            var enqueued = runtime.CoordinatorCommandsEnqueued;
            var waiting = database.Cloud.WaitForDurableAsync(target, TimeSpan.FromSeconds(1)).AsTask();
            Assert.True(runtime.CoordinatorCommandsEnqueued > enqueued);
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.False(await waiting.WaitAsync(TestTimeouts.Expected));
            Assert.False(blocking.IsCompleted);
        }
        finally
        {
            hold.Release();
        }
        await blocking.WaitAsync(TestTimeouts.Expected);
        _ = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(before, await database.Cloud.GetDurableSequenceAsync());
        Assert.True(await database.Cloud.WaitForDurableAsync(target, TestTimeouts.Expected));
        Assert.Equal("pending"u8.ToArray(), await ReadAsync(database, "target"));
        Assert.Equal("later"u8.ToArray(), await ReadAsync(database, "blocker"));
    }

    [Fact]
    public async Task ShouldNotAdvanceAcrossANewerUnacknowledgedWalWhenAnEarlierPublicationCompletes()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var secondUpload = new WalPutHoldHandler(handler);
        using var client = new HttpClient(secondUpload);
        using var firstAcknowledgement = new ArmedFailpointHold(Failpoint.AfterCloudWalUpload);
        await using var database = await PantsDatabase.OpenForTestingAsync(Options(cache.Path),
            new RuntimeDependencies(firstAcknowledgement, cloudHttpClient: client));
        var first = await CommitAsync(database, "first", "published");
        firstAcknowledgement.Arm();
        var firstWait = database.Cloud!.WaitForDurableAsync(first, TestTimeouts.Expected).AsTask();
        await firstAcknowledgement.Entered.WaitAsync(TestTimeouts.Expected);
        Task<bool>? secondWait = null;
        ulong second = 0;
        try
        {
            second = await CommitAsync(database, "second", "pending");
            secondUpload.Arm();
            secondWait = database.Cloud.WaitForDurableAsync(second, TestTimeouts.Expected).AsTask();
            _ = await database.Diagnostics.GetStorageLayoutAsync();
            var open = WalLedgerStore.LoadExisting(Path.Combine(cache.Path, "wal")).Active.Open;
            Assert.Equal(second, open.Prev.LastSeq);
            Assert.True(open.FirstSeq > second);
            firstAcknowledgement.Release();
            await secondUpload.Entered.WaitAsync(TestTimeouts.Expected);
            Assert.True(await firstWait.WaitAsync(TestTimeouts.Expected));
            var confirmed = await database.Cloud.GetDurableSequenceAsync();
            Assert.True(confirmed >= first);
            Assert.True(confirmed < second);
            Assert.False(secondWait.IsCompleted);
        }
        finally
        {
            firstAcknowledgement.Release();
            secondUpload.Release();
        }
        Assert.NotNull(secondWait);
        Assert.True(await secondWait.WaitAsync(TestTimeouts.Expected));
        Assert.True(await database.Cloud.GetDurableSequenceAsync() >= second);
        Assert.Equal("published"u8.ToArray(), await ReadAsync(database, "first"));
        Assert.Equal("pending"u8.ToArray(), await ReadAsync(database, "second"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldBoundConfirmationByOriginalCallerOrRuntimeBudgetAndRetainLateUpload(bool runtimeBudget)
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var hold = new WalPutHoldHandler(handler);
        using var client = new HttpClient(hold);
        var time = new ManualSchedulingTimeProvider();
        await using var database = await PantsDatabase.OpenForTestingAsync(Options(cache.Path),
            new RuntimeDependencies(cloudHttpClient: client, runtimeTimeProvider: time));
        var target = await CommitAsync(database, "key", "value");
        hold.Arm();
        var timeout = runtimeBudget ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(1);
        var waiting = database.Cloud!.WaitForDurableAsync(target, timeout).AsTask();
        await hold.Entered.WaitAsync(TestTimeouts.Expected);
        try
        {
            Assert.False(waiting.IsCompleted);
            Assert.True(await database.Cloud.GetDurableSequenceAsync() < target);
            time.Advance(runtimeBudget ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(1));
            Assert.False(await waiting.WaitAsync(TestTimeouts.Expected));
            Assert.True(await database.Cloud.GetDurableSequenceAsync() < target);
            Assert.Equal("value"u8.ToArray(), await ReadAsync(database, "key"));
        }
        finally
        {
            hold.Release();
        }
        Assert.True(await database.Cloud.WaitForDurableAsync(target, TestTimeouts.Expected));
        Assert.True(await database.Cloud.GetDurableSequenceAsync() >= target);
        Assert.False(await waiting);
        Assert.Equal(target, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
    }

    [Fact]
    public async Task ShouldCancelOnlyTheCallerAndKeepTheAcceptedCoveringUploadOwned()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var hold = new WalPutHoldHandler(handler);
        using var client = new HttpClient(hold);
        await using var database = await PantsDatabase.OpenForTestingAsync(Options(cache.Path),
            new RuntimeDependencies(cloudHttpClient: client));
        var target = await CommitAsync(database, "key", "value");
        hold.Arm();
        using var cancellation = new CancellationTokenSource();
        var waiting = database.Cloud!.WaitForDurableAsync(target, TestTimeouts.Expected, cancellation.Token).AsTask();
        await hold.Entered.WaitAsync(TestTimeouts.Expected);
        try
        {
            cancellation.Cancel();
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TestTimeouts.Expected));
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.True(await database.Cloud.GetDurableSequenceAsync() < target);
        }
        finally
        {
            hold.Release();
        }
        Assert.True(await database.Cloud.WaitForDurableAsync(target, TestTimeouts.Expected));
        Assert.Equal("value"u8.ToArray(), await ReadAsync(database, "key"));
    }

    [Fact]
    public async Task ShouldReportTransientPublicationAsNotYetCoveredAndAllowLaterConfirmation()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await PantsDatabase.OpenForTestingAsync(Options(cache.Path),
            new RuntimeDependencies(cloudHttpClient: client));
        var target = await CommitAsync(database, "key", "value");
        handler.FailWalWrites = true;
        try
        {
            Assert.False(await database.Cloud!.WaitForDurableAsync(target, TestTimeouts.Expected));
            Assert.True(handler.FailedWalWriteAttempts > 0);
            Assert.True(await database.Cloud.GetDurableSequenceAsync() < target);
        }
        finally
        {
            handler.FailWalWrites = false;
        }
        Assert.True(await database.Cloud!.WaitForDurableAsync(target, TestTimeouts.Expected));
        Assert.Equal("value"u8.ToArray(), await ReadAsync(database, "key"));
    }

    [Fact]
    public async Task ShouldValidateLeaseBeforeReportingAnAlreadyCoveredTarget()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var time = new ManualSchedulingTimeProvider();
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = Options(cache.Path).WithLeaseClockSkewTolerance(TimeSpan.FromMilliseconds(100))
            .WithLeaseTimeToLive(TimeSpan.FromSeconds(20));
        await using var database = await PantsDatabase.OpenForTestingAsync(options,
            new RuntimeDependencies(cloudHttpClient: client, runtimeTimeProvider: time,
                leaseClock: clock, leaseHeartbeatInterval: TimeSpan.FromMilliseconds(100)));
        var target = await CommitAsync(database, "key", "value");
        Assert.True(await database.Cloud!.WaitForDurableAsync(target, TestTimeouts.Expected));
        handler.BlockLeaseWrites();
        try
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await handler.WaitForBlockedLeaseWriteAsync();
            time.Advance(TimeSpan.FromSeconds(20) + TimeSpan.FromMilliseconds(1));
            Assert.False(database.Cloud.IsPrimaryLeaseHealthy);
            Assert.True(await database.Cloud.GetDurableSequenceAsync() >= target);
            await Assert.ThrowsAsync<PantsFencedException>(() => database.Cloud.WaitForDurableAsync(target, TimeSpan.Zero).AsTask());
        }
        finally
        {
            handler.ReleaseLeaseWrites();
        }
    }

    [Fact]
    public async Task ShouldFenceAnUncoveredWaitWhenAuthorityExpiresBeforeItsHeldUploadCompletes()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var hold = new WalPutHoldHandler(handler);
        using var client = new HttpClient(hold);
        var time = new ManualSchedulingTimeProvider();
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var database = await PantsDatabase.OpenForTestingAsync(Options(cache.Path)
                .WithLeaseClockSkewTolerance(TimeSpan.FromMilliseconds(100)).WithLeaseTimeToLive(TimeSpan.FromSeconds(20)),
            new RuntimeDependencies(cloudHttpClient: client, runtimeTimeProvider: time,
                leaseClock: clock, leaseHeartbeatInterval: TimeSpan.FromMilliseconds(100)));
        var target = await CommitAsync(database, "key", "value");
        hold.Arm();
        var waiting = database.Cloud!.WaitForDurableAsync(target, TestTimeouts.Expected).AsTask();
        await hold.Entered.WaitAsync(TestTimeouts.Expected);
        handler.BlockLeaseWrites();
        try
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await handler.WaitForBlockedLeaseWriteAsync();
            time.Advance(TimeSpan.FromSeconds(20) + TimeSpan.FromMilliseconds(1));
            await Assert.ThrowsAsync<PantsFencedException>(() => waiting.WaitAsync(TestTimeouts.Expected));
            Assert.True(await database.Cloud.GetDurableSequenceAsync() < target);
        }
        finally
        {
            hold.Release();
            handler.ReleaseLeaseWrites();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRecoverOnlyTheConfirmedCutFromThePublishedProviderImageAfterCacheLoss(bool confirmSecond)
    {
        using var cache = new TemporaryDirectory();
        using var successorCache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var hold = new WalPutHoldHandler(handler);
        using var client = new HttpClient(hold);
        var time = new ManualSchedulingTimeProvider();
        var epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await using var database = await PantsDatabase.OpenForTestingAsync(Options(cache.Path),
            new RuntimeDependencies(cloudHttpClient: client, runtimeTimeProvider: time, leaseClock: new ManualClock(epoch)));
        var sentinel = await CommitAsync(database, "sentinel", "published");
        Assert.True(await database.Cloud!.WaitForDurableAsync(sentinel, TestTimeouts.Expected));
        var target = await CommitAsync(database, "pending", "second");
        try
        {
            if (confirmSecond)
            {
                Assert.True(await database.Cloud.WaitForDurableAsync(target, TestTimeouts.Expected));
            }
            else
            {
                hold.Arm();
                var waiting = database.Cloud.WaitForDurableAsync(target, TimeSpan.FromSeconds(1)).AsTask();
                await hold.Entered.WaitAsync(TestTimeouts.Expected);
                time.Advance(TimeSpan.FromSeconds(1));
                Assert.False(await waiting.WaitAsync(TestTimeouts.Expected));
            }
            // A separate backend image contains only completed effects, before graceful shutdown
            // or the held upload can add anything. Its exact copied lease is expired by its clock.
            using var image = handler.CopyCommittedImage();
            using var imageClient = new HttpClient(image);
            await using var successor = await PantsDatabase.OpenForTestingAsync(Options(successorCache.Path),
                new RuntimeDependencies(cloudHttpClient: imageClient, leaseClock: new ManualClock(epoch.AddMinutes(5))));
            Assert.Equal("published"u8.ToArray(), await ReadAsync(successor, "sentinel"));
            Assert.Equal(confirmSecond ? "second"u8.ToArray() : null, await ReadAsync(successor, "pending"));
            var recovered = await successor.Cloud!.GetDurableSequenceAsync();
            Assert.True(recovered >= sentinel);
            Assert.Equal(confirmSecond, recovered >= target);
        }
        finally
        {
            hold.Release();
        }
    }

    static PantsOpenOptions Options(string path) => PantsOpenOptions.Cloud(path, new PantsCloudStorageLocation(
            new PantsAzureBlobProvider("account", "container", new Uri("https://storage.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=test")), "durable-target"))
        .WithBackgroundCompaction(false).WithLeaseTimeToLive(TimeSpan.FromMinutes(2))
        .WithStorageTimeout(TimeSpan.FromSeconds(5)).WithRuntimeResponseTimeout(TimeSpan.FromSeconds(10))
        .WithCloudWritePolicy(new PantsCloudWritePolicy(long.MaxValue, long.MaxValue, TimeSpan.FromHours(1), int.MaxValue));

    static async Task<ulong> CommitAsync(IPantsDatabase database, string key, string value)
    {
        await using var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        return await transaction.CommitWithSequenceAsync(PantsWriteOptions.CloudAsync);
    }

    static async Task<byte[]?> ReadAsync(IPantsDatabase database, string key)
    {
        await using var reader = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        return (await reader.GetAsync(TestBytes.FromString(key)))?.ToArray();
    }

    sealed class ArmedFailpointHold(Failpoint target) : IFailpointHandler, IDisposable
    {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly ManualResetEventSlim _release = new();
        int _armed;

        public Task Entered => _entered.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Hit(Failpoint failpoint)
        {
            if (failpoint == target && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _entered.TrySetResult();
                if (!_release.Wait(TestTimeouts.Expected))
                {
                    throw new TimeoutException($"The held {target} was not released.");
                }
            }
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
        }
    }

    sealed class WalPutHoldHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _armed;

        public Task Entered => _entered.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath.Contains("/wal/", StringComparison.Ordinal) &&
                request.RequestUri.AbsolutePath.EndsWith(".wal", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _entered.TrySetResult();
                await _release.Task;
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
