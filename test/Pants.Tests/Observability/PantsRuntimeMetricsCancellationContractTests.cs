namespace Pants.Tests;

public sealed class PantsRuntimeMetricsCancellationContractTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShouldReturnRuntimeMetricsGivenGenerousCallerDeadline()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var metrics = await database.GetRuntimeMetricsAsync(deadline.Token);

        Assert.Equal(PantsEngineHealth.Healthy, metrics.Health);
    }

    [Fact]
    public async Task ShouldRejectRuntimeMetricsBeforeAdmissionGivenCanceledCaller()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => database.GetRuntimeMetricsAsync(canceled.Token).AsTask());

        Assert.Equal(PantsEngineHealth.Healthy, (await database.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldRejectRuntimeMetricsGivenCompletedShutdown()
    {
        using var directory = new TemporaryDirectory();
        var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        await database.ShutdownAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<PantsAbortedException>(
            () => database.GetRuntimeMetricsAsync().AsTask());
    }

    [Fact]
    public async Task ShouldUnregisterResponseSlotWhenRuntimeMetricsResponseTimesOut()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new RuntimeMetricsResponseFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(failpoint));
        using var deadline = new CancellationTokenSource();
        var request = database.GetRuntimeMetricsAsync(deadline.Token).AsTask();

        try
        {
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => request.WaitAsync(AssertionTimeout));
        }
        finally
        {
            failpoint.Release();
        }

        var metrics = await database.GetRuntimeMetricsAsync()
            .AsTask().WaitAsync(AssertionTimeout);
        Assert.Equal(PantsEngineHealth.Healthy, metrics.Health);
    }

    [Fact]
    public async Task ShouldReturnRuntimeMetricsWhenStallClearsBeforeDeadline()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new RuntimeMetricsResponseFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(failpoint));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var request = database.GetRuntimeMetricsAsync(deadline.Token).AsTask();

        try
        {
            await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            failpoint.Release();

            var metrics = await request.WaitAsync(AssertionTimeout);

            Assert.Equal(PantsEngineHealth.Healthy, metrics.Health);
        }
        finally
        {
            failpoint.Release();
        }
    }

    [Fact]
    public async Task ShouldNotLeakResponseSlotsAcrossRepeatedTimeouts()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new RuntimeMetricsResponseFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(failpoint));

        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                using var deadline = new CancellationTokenSource();
                var request = database.GetRuntimeMetricsAsync(deadline.Token).AsTask();
                if (attempt == 0)
                {
                    await failpoint.WaitUntilEnteredAsync(AssertionTimeout);
                }

                deadline.CancelAfter(TimeSpan.FromMilliseconds(100));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => request.WaitAsync(AssertionTimeout));
            }
        }
        finally
        {
            failpoint.Release();
        }

        var metrics = await database.GetRuntimeMetricsAsync()
            .AsTask().WaitAsync(AssertionTimeout);
        Assert.Equal(PantsEngineHealth.Healthy, metrics.Health);
    }
}
