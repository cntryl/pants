namespace Pants.Tests;

public sealed class PantsRuntimeMetricsCancellationContractTests
{
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
}
