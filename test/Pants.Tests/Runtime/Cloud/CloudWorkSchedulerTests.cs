namespace Cntryl.Pants.Tests.Runtime.Cloud;

public sealed class CloudWorkSchedulerTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShouldCoalesceSignalsGivenCloudWorkIsAlreadyExecuting()
    {
        await using var worker = new RuntimeWorker(1);
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        await using var scheduler = new CloudWorkScheduler(
            worker,
            async cancellationToken =>
            {
                var execution = Interlocked.Increment(ref executions);
                if (execution == 1)
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    secondCompleted.TrySetResult();
                }
            });

        scheduler.Signal();
        await firstStarted.Task.WaitAsync(AssertionTimeout);
        for (var index = 0; index < 100; index++)
        {
            scheduler.Signal();
        }

        releaseFirst.SetResult();
        await secondCompleted.Task.WaitAsync(AssertionTimeout);
        await WaitForIdleAsync(scheduler);

        Assert.Equal(2, Volatile.Read(ref executions));
    }

    [Fact]
    public async Task ShouldRetryAutonomouslyGivenCloudWorkFails()
    {
        await using var worker = new RuntimeWorker(1);
        var firstAttempted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        await using var scheduler = new CloudWorkScheduler(
            worker,
            _ =>
            {
                if (Interlocked.Increment(ref executions) == 1)
                {
                    firstAttempted.SetResult();
                    throw new IOException("Injected cloud work failure.");
                }

                secondCompleted.SetResult();
                return ValueTask.CompletedTask;
            });

        scheduler.Signal();
        await firstAttempted.Task.WaitAsync(AssertionTimeout);
        await secondCompleted.Task.WaitAsync(AssertionTimeout);
        await WaitForIdleAsync(scheduler);

        Assert.Equal(2, Volatile.Read(ref executions));
    }

    [Fact]
    public async Task ShouldCancelRetriedWorkGivenSchedulerIsDisposed()
    {
        await using var worker = new RuntimeWorker(1);
        var secondStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCanceled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        await using var scheduler = new CloudWorkScheduler(
            worker,
            async cancellationToken =>
            {
                if (Interlocked.Increment(ref executions) == 1)
                {
                    throw new IOException("Injected cloud work failure.");
                }

                secondStarted.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    secondCanceled.SetResult();
                    throw;
                }
            });

        scheduler.Signal();
        await secondStarted.Task.WaitAsync(AssertionTimeout);

        await scheduler.DisposeAsync();

        await secondCanceled.Task.WaitAsync(AssertionTimeout);
        Assert.Equal(0, scheduler.Outstanding);
    }

    static async Task WaitForIdleAsync(CloudWorkScheduler scheduler)
    {
        using var timeout = new CancellationTokenSource(AssertionTimeout);
        while (scheduler.Outstanding != 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), timeout.Token);
        }
    }
}
