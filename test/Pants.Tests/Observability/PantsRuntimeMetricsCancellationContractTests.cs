using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Observability;

public sealed class PantsRuntimeMetricsCancellationContractTests
{
    [Fact]
    public async Task ShouldReturnRuntimeMetricsGivenGenerousCallerDeadline()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        using var deadline = new CancellationTokenSource(TestTimeouts.Expected);

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync(deadline.Token);

        Assert.Equal(PantsEngineHealth.Healthy, metrics.Health);
    }

    [Fact]
    public async Task ShouldRejectRuntimeMetricsBeforeAdmissionGivenCanceledCaller()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            database.Diagnostics.GetRuntimeMetricsAsync(canceled.Token).AsTask());

        Assert.Equal(PantsEngineHealth.Healthy, (await database.Diagnostics.GetRuntimeMetricsAsync()).Health);
    }

    [Fact]
    public async Task ShouldRejectRuntimeMetricsGivenCompletedShutdown()
    {
        using var directory = new TemporaryDirectory();
        var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        await database.ShutdownAsync(TestTimeouts.Expected);

        await Assert.ThrowsAsync<PantsBusyException>(() => database.Diagnostics.GetRuntimeMetricsAsync().AsTask());
    }

    [Fact]
    public async Task ShouldUnregisterResponseSlotWhenRuntimeMetricsResponseTimesOut()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new RuntimeMetricsResponseFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new RuntimeDependencies(failpoint));
        using var deadline = new CancellationTokenSource();
        var request = database.Diagnostics.GetRuntimeMetricsAsync(deadline.Token).AsTask();

        try
        {
            await failpoint.WaitUntilEnteredAsync(TestTimeouts.Expected);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TestTimeouts.Expected));
        }
        finally
        {
            failpoint.Release();
        }

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync()
            .AsTask().WaitAsync(TestTimeouts.Expected);
        Assert.Equal(PantsEngineHealth.Healthy, metrics.Health);
    }

    [Fact]
    public async Task ShouldReturnRuntimeMetricsWhenStallClearsBeforeDeadline()
    {
        using var directory = new TemporaryDirectory();
        using var failpoint = new RuntimeMetricsResponseFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new RuntimeDependencies(failpoint));
        using var deadline = new CancellationTokenSource(TestTimeouts.Expected);
        var request = database.Diagnostics.GetRuntimeMetricsAsync(deadline.Token).AsTask();

        try
        {
            await failpoint.WaitUntilEnteredAsync(TestTimeouts.Expected);
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            failpoint.Release();

            var metrics = await request.WaitAsync(TestTimeouts.Expected);

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
            new RuntimeDependencies(failpoint));

        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                using var deadline = new CancellationTokenSource();
                var request = database.Diagnostics.GetRuntimeMetricsAsync(deadline.Token).AsTask();
                if (attempt == 0)
                {
                    await failpoint.WaitUntilEnteredAsync(TestTimeouts.Expected);
                }

                deadline.CancelAfter(TimeSpan.FromMilliseconds(100));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TestTimeouts.Expected));
            }
        }
        finally
        {
            failpoint.Release();
        }

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync()
            .AsTask().WaitAsync(TestTimeouts.Expected);
        Assert.Equal(PantsEngineHealth.Healthy, metrics.Health);
    }
}
