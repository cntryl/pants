namespace Cntryl.Pants.Cloud;

public sealed class CloudDurabilityWaitShutdownTests
{
    [Fact]
    public async Task ShouldEndObservationDuringHeldRequestAdmissionAndKeepShutdownStickyAfterLatePublication()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong frontier = 0;
        var requests = 0;
        var waits = new CloudDurabilityWaitState(TimeProvider.System, TestTimeouts.Expected,
            () => frontier, static () => { }, async (_, _, _) =>
            {
                Interlocked.Increment(ref requests);
                entered.TrySetResult();
                await release.Task;
                completed.TrySetResult();
                throw new PantsIOException("Late request completion remains owned.");
            });
        var accepted = waits.WaitAsync(1, TestTimeouts.Expected, CancellationToken.None).AsTask();
        await entered.Task.WaitAsync(TestTimeouts.Expected);
        try
        {
            waits.StopForShutdown();
            Assert.False(await accepted.WaitAsync(TestTimeouts.Expected));
            Assert.False(completed.Task.IsCompleted);
            frontier = 1;
            waits.Notify();
            Assert.False(await waits.WaitAsync(1, TestTimeouts.Expected, CancellationToken.None));
            waits.StopForShutdown();
            Assert.Equal(1, requests);
        }
        finally
        {
            release.TrySetResult();
        }
        await completed.Task.WaitAsync(TestTimeouts.Expected);
        Assert.False(await accepted);
    }

    [Fact]
    public async Task ShouldReturnTheShutdownOutcomeWhenPreflightAlreadyEnteredAuthorityValidation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var requests = 0;
        var waits = new CloudDurabilityWaitState(TimeProvider.System, TestTimeouts.Expected,
            static () => 0, () =>
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TestTimeouts.Expected));
                throw new PantsFencedException("Runtime disposal followed accepted shutdown.");
            }, (_, _, _) =>
            {
                Interlocked.Increment(ref requests);
                return ValueTask.CompletedTask;
            });
        var racing = Task.Run(async () => await waits.WaitAsync(1, TestTimeouts.Expected, CancellationToken.None));
        await entered.Task.WaitAsync(TestTimeouts.Expected);
        try
        {
            waits.StopForShutdown();
        }
        finally
        {
            release.Set();
        }
        Assert.False(await racing.WaitAsync(TestTimeouts.Expected));
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task ShouldPreservePreCanceledCallerIdentityAfterShutdownStoppedNewObservers()
    {
        var waits = new CloudDurabilityWaitState(TimeProvider.System, TestTimeouts.Expected,
            static () => 1, static () => { }, static (_, _, _) => ValueTask.CompletedTask);
        waits.StopForShutdown();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            waits.WaitAsync(1, TestTimeouts.Expected, cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }
}
