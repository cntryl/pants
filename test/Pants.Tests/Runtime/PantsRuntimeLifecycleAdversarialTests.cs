namespace Cntryl.Pants.Tests.Runtime;

public sealed class PantsRuntimeLifecycleAdversarialTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShouldReleaseDiskStoreWhenRunLoopFaultsDuringDisposal()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path);
        var failpoint = new RunLoopFaultFailpointHandler();
        var actor = new Actor(
            options,
            new MonotonicPantsClock(options.TtlClock),
            new RuntimeTelemetry(),
            new RuntimeDependencies(failpoint));

        _ = await actor.GetRecoveryMetricsAsync(CancellationToken.None);
        failpoint.Arm();
        _ = await actor.GetRecoveryMetricsAsync(CancellationToken.None);
        await failpoint.WaitUntilFaultedAsync(AssertionTimeout);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            actor.DisposeAsync().AsTask().WaitAsync(AssertionTimeout));
        Assert.Contains(nameof(Failpoint.AfterRuntimeCommandExecution), failure.Message);
        Assert.True(actor.AreOwnedRuntimeResourcesDisposed);

        await using var reopened = await PantsDatabase.OpenAsync(options);
    }

    [Fact]
    public async Task ShouldRemainUsableAndAllowShutdownRetryAfterPreparationFailure()
    {
        using var directory = new TemporaryDirectory();
        var failpoint = new RetryingShutdownBoundaryFailpointHandler();
        var options = PantsOpenOptions.Local(directory.Path);
        var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoint));
        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("before-failure"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        await Assert.ThrowsAsync<PantsIOException>(() =>
            database.ShutdownAsync(AssertionTimeout).AsTask());

        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("after-failure"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        await database.ShutdownAsync(AssertionTimeout);
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var read = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "value",
            TestBytes.ToText((await read.GetAsync("after-failure"u8.ToArray()))!.Value));
    }

    [Fact]
    public async Task ShouldShareOneShutdownExecutionAcrossConcurrentCallers()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new GatedShutdownBoundaryFailpointHandler();
        var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new RuntimeDependencies(failpoint));
        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("buffered"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        var shutdowns = Enumerable.Range(0, 16)
            .Select(_ => database.ShutdownAsync(AssertionTimeout).AsTask())
            .ToArray();
        await failpoint.WaitUntilEnteredAsync(AssertionTimeout);

        Assert.Equal(1, failpoint.HitCount);
        Assert.All(shutdowns, static shutdown => Assert.False(shutdown.IsCompleted));

        failpoint.Release();
        await Task.WhenAll(shutdowns).WaitAsync(AssertionTimeout);
        Assert.Equal(1, failpoint.HitCount);
    }

    [Fact]
    public async Task ShouldBlockAndDrainMixedTrafficBeyondCoordinatorCapacity()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new RuntimeMetricsResponseFailpointHandler();
        var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path).WithCoordinatorQueueCapacityForTesting(2),
            new RuntimeDependencies(failpoint));
        var transactions = new List<IPantsTransaction>();
        for (var index = 0; index < 10; index++)
        {
            var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(
                TestBytes.FromString($"key-{index}"),
                TestBytes.FromString($"value-{index}"));
            transactions.Add(transaction);
        }

        var blocker = database.GetRuntimeMetricsAsync().AsTask();
        await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
        var metrics = Enumerable.Range(0, 10)
            .Select(_ => database.GetRuntimeMetricsAsync().AsTask())
            .ToArray();
        var commits = transactions
            .Select(transaction => transaction.CommitAsync(PantsWriteOptions.Sync).AsTask())
            .ToArray();

        await Task.Delay(100);
        Assert.All(metrics, static operation => Assert.False(operation.IsCompleted));
        Assert.All(commits, static operation => Assert.False(operation.IsCompleted));

        failpoint.Release();
        _ = await blocker.WaitAsync(AssertionTimeout);
        await Task.WhenAll(metrics).WaitAsync(AssertionTimeout);
        await Task.WhenAll(commits).WaitAsync(AssertionTimeout);
        foreach (var transaction in transactions)
        {
            await transaction.DisposeAsync();
        }

        await using var read = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 10; index++)
        {
            Assert.Equal(
                $"value-{index}",
                TestBytes.ToText((await read.GetAsync(
                    TestBytes.FromString($"key-{index}")))!.Value));
        }

        await read.RollbackAsync();
        await database.ShutdownAsync(AssertionTimeout);
    }
}
