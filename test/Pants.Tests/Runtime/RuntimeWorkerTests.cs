namespace Cntryl.Pants.Runtime;

[Collection(RuntimeDiagnosticsTestGroup.Name)]
public sealed class RuntimeWorkerTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShouldTrackOutstandingWorkFromAdmissionThroughCompletion()
    {
        await using var worker = new RuntimeWorker(1);
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = await worker.ScheduleAsync(async _ =>
        {
            started.SetResult();
            await release.Task;
        });
        var second = Task.CompletedTask;
        var thirdAdmission = Task.FromResult(Task.CompletedTask);

        try
        {
            await started.Task.WaitAsync(AssertionTimeout);
            second = await worker.ScheduleAsync(static _ => ValueTask.CompletedTask);
            thirdAdmission = worker
                .ScheduleAsync(static _ => ValueTask.CompletedTask)
                .AsTask();
            Assert.False(thirdAdmission.IsCompleted);
            Assert.Equal(3, worker.Outstanding);
        }
        finally
        {
            release.TrySetResult();
        }

        var third = await thirdAdmission;
        await Task.WhenAll(first, second, third);
        using var timeout = new CancellationTokenSource(AssertionTimeout);
        while (worker.Outstanding != 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), timeout.Token);
        }

        Assert.Equal(0, worker.Outstanding);
    }

    [Fact]
    public async Task ShouldObserveCallerCancellationDuringExecution()
    {
        await using var worker = new RuntimeWorker(1);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = worker.ExecuteAsync(
            async workerCancellationToken =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(workerCancellationToken);
            },
            cancellation.Token).AsTask();

        try
        {
            await started.Task.WaitAsync(AssertionTimeout);
            cancellation.Cancel();

            var exception =
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(AssertionTimeout));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task ShouldCompleteAdmittedScheduledWorkGivenAdmissionTokenCancels()
    {
        await using var worker = new RuntimeWorker(1);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = await worker.ScheduleAsync(
            async workerCancellationToken =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(workerCancellationToken);
            },
            cancellation.Token);

        await started.Task.WaitAsync(AssertionTimeout);
        cancellation.Cancel();
        release.TrySetResult();

        await execution.WaitAsync(AssertionTimeout);
    }

    [Fact]
    public async Task ShouldRestoreCountersGivenBlockedAdmissionIsCancelled()
    {
        await using var worker = new RuntimeWorker(1);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var first = await worker.ScheduleAsync(async _ =>
        {
            started.TrySetResult();
            await release.Task;
        });
        await started.Task.WaitAsync(AssertionTimeout);
        var second = await worker.ScheduleAsync(static _ => ValueTask.CompletedTask);
        var blocked = worker.ScheduleAsync(
                static _ => ValueTask.CompletedTask,
                cancellation.Token)
            .AsTask();
        try
        {
            Assert.False(blocked.IsCompleted);
            Assert.Equal(3, worker.Outstanding);
            Assert.Equal(2, worker.QueueDepth);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                blocked.WaitAsync(AssertionTimeout));

            Assert.Equal(2, worker.Outstanding);
            Assert.Equal(1, worker.QueueDepth);
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(first, second).WaitAsync(AssertionTimeout);
    }

    [Fact]
    public async Task ShouldBoundDisposeGivenOperationIgnoresCancellation()
    {
        var worker = new RuntimeWorker(1, TimeSpan.FromMilliseconds(50));
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = await worker.ScheduleAsync(async _ =>
        {
            started.TrySetResult();
            await release.Task;
        });
        await started.Task.WaitAsync(AssertionTimeout);

        try
        {
            var failure = await Assert.ThrowsAsync<PantsTimeoutException>(() =>
                worker.DisposeAsync().AsTask().WaitAsync(AssertionTimeout));
            Assert.Equal(PantsErrorCode.Timeout, failure.Code);
        }
        finally
        {
            release.TrySetResult();
        }

        await operation.WaitAsync(AssertionTimeout);
    }
}
