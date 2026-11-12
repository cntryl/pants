using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class PantsCloudTargetWaitAuditTests
{
    [Fact]
    public async Task ShouldRejectAnUncoverableFutureTargetWithoutWalWork()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        await using var database = await PantsDatabase.OpenForTestingAsync(Options(cache.Path),
            new RuntimeDependencies(cloudHttpClient: client));
        await Assert.ThrowsAsync<PantsInternalException>(() => database.Cloud!
            .WaitForDurableAsync(ulong.MaxValue, TestTimeouts.Expected).AsTask());
    }

    [Fact]
    public async Task ShouldRejectAnUncoverableFutureTargetDespiteAnUnrelatedAcceptedUpload()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var hold = new WalPutHoldHandler(handler);
        using var client = new HttpClient(hold);
        var time = new ManualSchedulingTimeProvider();
        await using var database = await PantsDatabase.OpenForTestingAsync(Options(cache.Path),
            new RuntimeDependencies(cloudHttpClient: client, runtimeTimeProvider: time));
        var target = await CommitAsync(database);
        hold.Arm();
        var accepted = database.Cloud!.WaitForDurableAsync(target, TestTimeouts.Expected).AsTask();
        await hold.Entered.WaitAsync(TestTimeouts.Expected);
        using var cancellation = new CancellationTokenSource();
        Task<bool>? future = null;
        try
        {
            future = database.Cloud.WaitForDurableAsync(ulong.MaxValue, TimeSpan.FromSeconds(1), cancellation.Token).AsTask();
            _ = await database.Diagnostics.GetStorageLayoutAsync();
            Assert.False(accepted.IsCompleted);
            Assert.True(await database.Cloud.GetDurableSequenceAsync() < target);
            await Assert.ThrowsAsync<PantsInternalException>(() => future.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            cancellation.Cancel();
            hold.Release();
            if (future is not null)
            {
                _ = await Record.ExceptionAsync(() => future);
            }
            _ = await Record.ExceptionAsync(() => accepted);
        }
        Assert.True(await accepted.WaitAsync(TestTimeouts.Expected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldReturnFalseForAcceptedWaitsWhenCloseBeginsBeforeTheirHeldUploadFinishes(bool dispose)
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var hold = new WalPutHoldHandler(handler);
        using var client = new HttpClient(hold);
        await using var database = await PantsDatabase.OpenForTestingAsync(Options(cache.Path),
            new RuntimeDependencies(cloudHttpClient: client));
        var target = await CommitAsync(database);
        hold.Arm();
        var accepted = database.Cloud!.WaitForDurableAsync(target, TestTimeouts.Expected).AsTask();
        await hold.Entered.WaitAsync(TestTimeouts.Expected);
        var other = database.Cloud.WaitForDurableAsync(target, TestTimeouts.Expected).AsTask();
        _ = await database.Diagnostics.GetStorageLayoutAsync();
        var shutdown = dispose ? database.DisposeAsync().AsTask() : database.ShutdownAsync(TestTimeouts.Expected).AsTask();
        try
        {
            Assert.False(shutdown.IsCompleted);
            await Assert.ThrowsAsync<PantsBusyException>(() => database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly).AsTask());
            await Assert.ThrowsAsync<PantsBusyException>(() => database.Cloud.WaitForDurableAsync(target, TestTimeouts.Expected).AsTask());
            Assert.False(await accepted.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.False(await other.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.False(shutdown.IsCompleted);
        }
        finally
        {
            hold.Release();
            await shutdown.WaitAsync(TestTimeouts.Expected);
            _ = await Record.ExceptionAsync(() => accepted);
            _ = await Record.ExceptionAsync(() => other);
        }
        Assert.False(await accepted);
        Assert.False(await other);
    }

    [Fact]
    public async Task ShouldKeepACompletedConfirmationSuccessfulWhenShutdownFollowsAcknowledgement()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var hold = new WalPutHoldHandler(handler);
        using var client = new HttpClient(hold);
        await using var database = await PantsDatabase.OpenForTestingAsync(Options(cache.Path),
            new RuntimeDependencies(cloudHttpClient: client));
        var target = await CommitAsync(database);
        hold.Arm();
        var accepted = database.Cloud!.WaitForDurableAsync(target, TestTimeouts.Expected).AsTask();
        try
        {
            await hold.Entered.WaitAsync(TestTimeouts.Expected);
        }
        finally
        {
            hold.Release();
        }
        Assert.True(await accepted.WaitAsync(TestTimeouts.Expected));
        Assert.True(await database.Cloud.GetDurableSequenceAsync() >= target);
        await database.ShutdownAsync(TestTimeouts.Expected);
        Assert.True(await accepted);
    }

    [Fact]
    public async Task ShouldKeepAnAcceptedWaitAndRetainedReaderLiveWhenShutdownAdmissionIsBusy()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var hold = new WalPutHoldHandler(handler);
        using var client = new HttpClient(hold);
        await using var database = await PantsDatabase.OpenForTestingAsync(Options(cache.Path),
            new RuntimeDependencies(cloudHttpClient: client));
        var target = await CommitAsync(database);
        await using var reader = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        hold.Arm();
        var accepted = database.Cloud!.WaitForDurableAsync(target, TestTimeouts.Expected).AsTask();
        await hold.Entered.WaitAsync(TestTimeouts.Expected);
        try
        {
            await Assert.ThrowsAsync<PantsBusyException>(() => database.ShutdownAsync(TestTimeouts.Expected).AsTask());
            Assert.False(accepted.IsCompleted);
            Assert.Equal("value"u8.ToArray(), (await reader.GetAsync("key"u8.ToArray()))?.ToArray());
        }
        finally
        {
            hold.Release();
        }
        Assert.True(await accepted.WaitAsync(TestTimeouts.Expected));
        Assert.Equal("value"u8.ToArray(), (await reader.GetAsync("key"u8.ToArray()))?.ToArray());
        await reader.DisposeAsync();
        await database.ShutdownAsync(TestTimeouts.Expected);
    }

    [Fact]
    public async Task ShouldPreserveCallerCancellationWhenShutdownFollowsAnAcceptedCancellation()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var hold = new WalPutHoldHandler(handler);
        using var client = new HttpClient(hold);
        await using var database = await PantsDatabase.OpenForTestingAsync(Options(cache.Path),
            new RuntimeDependencies(cloudHttpClient: client));
        var target = await CommitAsync(database);
        hold.Arm();
        using var cancellation = new CancellationTokenSource();
        var accepted = database.Cloud!.WaitForDurableAsync(target, TestTimeouts.Expected, cancellation.Token).AsTask();
        await hold.Entered.WaitAsync(TestTimeouts.Expected);
        Task? shutdown = null;
        try
        {
            cancellation.Cancel();
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accepted);
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            shutdown = database.ShutdownAsync(TestTimeouts.Expected).AsTask();
            Assert.False(shutdown.IsCompleted);
        }
        finally
        {
            hold.Release();
        }
        Assert.NotNull(shutdown);
        await shutdown.WaitAsync(TestTimeouts.Expected);
        var finalFailure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accepted);
        Assert.Equal(cancellation.Token, finalFailure.CancellationToken);
    }

    [Fact]
    public async Task ShouldRejectAnUncoverableFutureTargetWhenSealingOnlyCreatesAShorterUpload()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var hold = new WalPutHoldHandler(handler);
        using var client = new HttpClient(hold);
        await using var database = await PantsDatabase.OpenForTestingAsync(Options(cache.Path),
            new RuntimeDependencies(cloudHttpClient: client));
        var target = await CommitAsync(database);
        hold.Arm();
        using var cancellation = new CancellationTokenSource();
        var future = database.Cloud!.WaitForDurableAsync(ulong.MaxValue, TestTimeouts.Expected, cancellation.Token).AsTask();
        await hold.Entered.WaitAsync(TestTimeouts.Expected);
        try
        {
            await Assert.ThrowsAsync<PantsInternalException>(() => future.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.True(await database.Cloud.GetDurableSequenceAsync() < target);
        }
        finally
        {
            cancellation.Cancel();
            hold.Release();
            _ = await Record.ExceptionAsync(() => future);
        }
        Assert.True(await database.Cloud.WaitForDurableAsync(target, TestTimeouts.Expected));
    }

    [Fact]
    public async Task ShouldCancelOnlyOneObserverWhileOtherAcceptedWaitsKeepTheUploadOwned()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var hold = new WalPutHoldHandler(handler);
        using var client = new HttpClient(hold);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            Options(cache.Path).WithCoordinatorQueueCapacityForTesting(1),
            new RuntimeDependencies(cloudHttpClient: client));
        var target = await CommitAsync(database);
        hold.Arm();
        using var cancellation = new CancellationTokenSource();
        var first = database.Cloud!.WaitForDurableAsync(target, TestTimeouts.Expected, cancellation.Token).AsTask();
        await hold.Entered.WaitAsync(TestTimeouts.Expected);
        Task<bool>? replacement = null;
        Task<bool>? other = null;
        try
        {
            other = database.Cloud.WaitForDurableAsync(target, TestTimeouts.Expected).AsTask();
            _ = await database.Diagnostics.GetStorageLayoutAsync();
            Assert.False(other.IsCompleted);
            cancellation.Cancel();
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            replacement = database.Cloud.WaitForDurableAsync(target, TestTimeouts.Expected).AsTask();
            _ = await database.Diagnostics.GetStorageLayoutAsync();
            Assert.False(replacement.IsCompleted);
            Assert.True(await database.Cloud.GetDurableSequenceAsync() < target);
        }
        finally
        {
            hold.Release();
        }
        Assert.NotNull(replacement);
        Assert.NotNull(other);
        Assert.True(await other.WaitAsync(TestTimeouts.Expected));
        Assert.True(await replacement.WaitAsync(TestTimeouts.Expected));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task ShouldKeepAcceptedObserversPendingBeyondCoordinatorQueueCapacityUntilTheirSharedUploadCompletes(int count)
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var hold = new WalPutHoldHandler(handler);
        using var client = new HttpClient(hold);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            Options(cache.Path).WithCoordinatorQueueCapacityForTesting(1),
            new RuntimeDependencies(cloudHttpClient: client));
        var target = await CommitAsync(database);
        hold.Arm();
        List<Task<bool>> observers = [database.Cloud!.WaitForDurableAsync(target, TestTimeouts.Expected).AsTask()];
        await hold.Entered.WaitAsync(TestTimeouts.Expected);
        try
        {
            for (var index = 1; index < count; index++)
            {
                observers.Add(database.Cloud.WaitForDurableAsync(target, TestTimeouts.Expected).AsTask());
                // This coordinator response is ordered after this observer's request admission.
                _ = await database.Diagnostics.GetStorageLayoutAsync();
                Assert.All(observers, static observer => Assert.False(observer.IsCompleted));
            }
        }
        finally
        {
            hold.Release();
        }
        Assert.All(await Task.WhenAll(observers).WaitAsync(TestTimeouts.Expected), Assert.True);
    }

    static PantsOpenOptions Options(string path) => PantsOpenOptions.Cloud(path, new PantsCloudStorageLocation(
            new PantsAzureBlobProvider("account", "container", new Uri("https://storage.example.test"),
                new PantsAzureCredentialSource.SasToken("sig=test")), "target-wait-audit"))
        .WithBackgroundCompaction(false).WithLeaseTimeToLive(TimeSpan.FromMinutes(2))
        .WithStorageTimeout(TimeSpan.FromSeconds(5)).WithRuntimeResponseTimeout(TimeSpan.FromSeconds(10))
        .WithCloudWritePolicy(new PantsCloudWritePolicy(long.MaxValue, long.MaxValue, TimeSpan.FromHours(1), int.MaxValue));

    static async Task<ulong> CommitAsync(IPantsDatabase database)
    {
        await using var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
        return await transaction.CommitWithSequenceAsync(PantsWriteOptions.CloudAsync);
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
