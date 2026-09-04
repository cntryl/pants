namespace Cntryl.Pants.Runtime;

public sealed class ChannelRuntimeServiceTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShouldBoundAdmissionWhilePreservingTypedDispatchOrder()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new TestRuntimeService(1, started, release);

        var first = await service.ScheduleAsync(new TestRuntimeRequest(1, true));
        await started.Task.WaitAsync(AssertionTimeout);
        var second = await service.ScheduleAsync(new TestRuntimeRequest(2, false));
        var thirdAdmission = service
            .ScheduleAsync(new TestRuntimeRequest(3, false))
            .AsTask();

        Assert.False(thirdAdmission.IsCompleted);
        Assert.Equal(3, service.Outstanding);

        release.SetResult();
        var third = await thirdAdmission.WaitAsync(AssertionTimeout);
        var results = await Task.WhenAll(first, second, third).WaitAsync(AssertionTimeout);

        Assert.Equal([1, 2, 3], results);
        Assert.Equal([1, 2, 3], service.ExecutedRequests);
    }

    [Fact]
    public async Task ShouldPropagateTypedDispatchFailureToCaller()
    {
        await using var service = new TestRuntimeService(1);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteAsync(new TestRuntimeRequest(7, false, true))
                .AsTask());

        Assert.Equal("Request 7 failed.", exception.Message);
        Assert.Equal(1, service.Failures);
        Assert.Equal(0, service.Completed);
    }

    [Fact]
    public async Task ShouldDrainAdmittedRequestsBeforeShutdownAndRejectLaterAdmission()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new TestRuntimeService(1, started, release);
        var first = await service.ScheduleAsync(new TestRuntimeRequest(1, true));
        await started.Task.WaitAsync(AssertionTimeout);
        var second = await service.ScheduleAsync(new TestRuntimeRequest(2, false));

        var shutdown = service.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);

        release.SetResult();
        await shutdown.WaitAsync(AssertionTimeout);

        var results = await Task.WhenAll(first, second);
        Assert.Equal([1, 2], results);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            service.ExecuteAsync(new TestRuntimeRequest(3, false)).AsTask());
    }
}
