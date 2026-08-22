namespace Pants.Tests;

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
}
