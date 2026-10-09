namespace Cntryl.Pants.Runtime.Cloud;

public sealed class CloudWorkSchedulerTests
{
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
        await firstStarted.Task.WaitAsync(TestTimeouts.Expected);
        for (var index = 0; index < 100; index++)
        {
            scheduler.Signal();
        }

        releaseFirst.SetResult();
        await secondCompleted.Task.WaitAsync(TestTimeouts.Expected);
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
        await firstAttempted.Task.WaitAsync(TestTimeouts.Expected);
        await secondCompleted.Task.WaitAsync(TestTimeouts.Expected);
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
        await secondStarted.Task.WaitAsync(TestTimeouts.Expected);

        await scheduler.DisposeAsync();

        await secondCanceled.Task.WaitAsync(TestTimeouts.Expected);
        Assert.Equal(0, scheduler.Outstanding);
    }

    [Fact]
    public async Task ShouldRunCloudWorkAgainUntilItReportsCompletionWithoutAnotherSignal()
    {
        await using var worker = new RuntimeWorker(1);
        var outcomes = new Queue<CloudWorkOutcome>(
        [
            CloudWorkOutcome.Continue,
            CloudWorkOutcome.RetryLater,
            CloudWorkOutcome.Continue,
            CloudWorkOutcome.Completed
        ]);
        var executions = 0;
        await using var scheduler = new CloudWorkScheduler(
            worker,
            _ =>
            {
                Interlocked.Increment(ref executions);
                return ValueTask.FromResult(outcomes.Dequeue());
            });

        scheduler.Signal();
        await WaitForIdleAsync(scheduler);

        Assert.Equal(4, Volatile.Read(ref executions));
        Assert.Empty(outcomes);
    }

    static async Task WaitForIdleAsync(CloudWorkScheduler scheduler)
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.Expected);
        while (scheduler.Outstanding != 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), timeout.Token);
        }
    }
}
