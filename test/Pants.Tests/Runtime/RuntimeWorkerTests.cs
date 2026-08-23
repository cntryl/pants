namespace Pants.Tests;

[Collection(RuntimeDiagnosticsTestGroup.Name)]
public sealed class RuntimeWorkerTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShouldTrackOutstandingWorkFromAdmissionThroughCompletion()
    {
        await using var worker = new RuntimeWorker(capacity: 1);
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
        await using var worker = new RuntimeWorker(capacity: 1);
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

            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => execution.WaitAsync(AssertionTimeout));
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
        await using var worker = new RuntimeWorker(capacity: 1);
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
}
