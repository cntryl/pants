using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudDurabilityWaitOutcomeTests
{
    [Fact]
    public async Task ShouldKeepShutdownOutcomeWhenLaterCoverageArrivesDuringAnAdmittedFrontierRead()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var requests = 0;
        ulong frontier = 0;
        var waits = new CloudDurabilityWaitState(TimeProvider.System, TestTimeouts.Expected, () =>
        {
            if (Interlocked.Increment(ref reads) == 2)
            {
                entered.TrySetResult();
                Assert.True(release.Wait(TestTimeouts.Expected));
            }
            return Volatile.Read(ref frontier);
        }, static () => { }, (_, _, _) =>
        {
            Interlocked.Increment(ref requests);
            return ValueTask.CompletedTask;
        });
        var accepted = Task.Run(async () => await waits.WaitAsync(1, TestTimeouts.Expected, CancellationToken.None));
        await entered.Task.WaitAsync(TestTimeouts.Expected);
        try
        {
            waits.StopForShutdown();
            Volatile.Write(ref frontier, 1);
            waits.Notify();
        }
        finally
        {
            release.Set();
        }
        Assert.False(await accepted.WaitAsync(TestTimeouts.Expected));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(Competitor.Shutdown)]
    [InlineData(Competitor.Deadline)]
    [InlineData(Competitor.CallerCancellation)]
    public async Task ShouldKeepAcknowledgementWhenACompetingOutcomeArrivesBeforeAdmissionResponseResumes(Competitor later)
    {
        await using var request = new HeldAcceptedRequest();
        using var cancellation = new CancellationTokenSource();
        var accepted = request.Waits.WaitAsync(1, request.Budget, cancellation.Token).AsTask();
        await request.Entered.WaitAsync(TestTimeouts.Expected);
        request.Acknowledge();
        request.Compete(later, cancellation);
        Assert.True(await accepted.WaitAsync(TestTimeouts.Expected));
        Assert.False(request.PhysicalCompletion.IsCompleted);
    }

    [Theory]
    [InlineData(Competitor.Shutdown)]
    [InlineData(Competitor.Deadline)]
    [InlineData(Competitor.CallerCancellation)]
    public async Task ShouldKeepAnEarlierCompetingOutcomeWhenItsAbandonedTargetIsLaterAcknowledged(Competitor earlier)
    {
        await using var request = new HeldAcceptedRequest();
        using var cancellation = new CancellationTokenSource();
        var accepted = request.Waits.WaitAsync(1, request.Budget, cancellation.Token).AsTask();
        await request.Entered.WaitAsync(TestTimeouts.Expected);
        request.Compete(earlier, cancellation);
        request.Acknowledge();
        if (earlier == Competitor.CallerCancellation)
        {
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accepted.WaitAsync(TestTimeouts.Expected));
            Assert.Equal(cancellation.Token, failure.CancellationToken);
        }
        else
        {
            Assert.False(await accepted.WaitAsync(TestTimeouts.Expected));
        }
        Assert.False(request.PhysicalCompletion.IsCompleted);
        Assert.Equal(earlier != Competitor.Shutdown,
            await request.Waits.WaitAsync(1, TimeSpan.Zero, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldKeepTheFirstRoutedFailureOrShutdownOutcomeDespiteLaterAcknowledgement(bool failureFirst)
    {
        await using var request = new HeldAcceptedRequest();
        var accepted = request.Waits.WaitAsync(1, request.Budget, CancellationToken.None).AsTask();
        await request.Entered.WaitAsync(TestTimeouts.Expected);
        var expected = new PantsInternalException("The accepted publication failed.");
        if (failureFirst)
        {
            request.Waits.Notify(expected);
            request.Waits.StopForShutdown();
        }
        else
        {
            request.Waits.StopForShutdown();
            request.Waits.Notify(expected);
        }
        request.Acknowledge();
        if (failureFirst)
        {
            Assert.Same(expected, await Assert.ThrowsAsync<PantsInternalException>(() => accepted.WaitAsync(TestTimeouts.Expected)));
        }
        else
        {
            Assert.False(await accepted.WaitAsync(TestTimeouts.Expected));
        }
        Assert.False(request.PhysicalCompletion.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldAbandonDuringInitialAuthorityValidationWithoutLaterSubmitting(bool cancelCaller)
    {
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new ManualSchedulingTimeProvider();
        var requests = 0;
        var waits = new CloudDurabilityWaitState(time, TestTimeouts.Expected, static () => 0, () =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TestTimeouts.Expected));
            throw new PantsFencedException("A later validation failure must not replace abandonment.");
        }, (_, _, _) =>
        {
            Interlocked.Increment(ref requests);
            return ValueTask.CompletedTask;
        });
        var accepted = Task.Run(async () => await waits.WaitAsync(1, TimeSpan.FromSeconds(1), cancellation.Token));
        await entered.Task.WaitAsync(TestTimeouts.Expected);
        try
        {
            if (cancelCaller)
            {
                cancellation.Cancel();
            }
            else
            {
                time.Advance(TimeSpan.FromSeconds(1));
            }
        }
        finally
        {
            release.Set();
        }
        if (cancelCaller)
        {
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accepted.WaitAsync(TestTimeouts.Expected));
            Assert.Equal(cancellation.Token, failure.CancellationToken);
        }
        else
        {
            Assert.False(await accepted.WaitAsync(TestTimeouts.Expected));
        }
        Assert.Equal(0, requests);
    }

    public enum Competitor
    {
        Shutdown,
        Deadline,
        CallerCancellation
    }

    sealed class HeldAcceptedRequest : IAsyncDisposable
    {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly ManualSchedulingTimeProvider _time = new();
        ulong _frontier;

        public HeldAcceptedRequest()
        {
            Waits = new CloudDurabilityWaitState(_time, TestTimeouts.Expected,
                () => Volatile.Read(ref _frontier), static () => { }, async (_, _, admitted, _) =>
                {
                    admitted(null);
                    _entered.TrySetResult();
                    await _release.Task;
                    _completed.TrySetResult();
                });
        }

        public CloudDurabilityWaitState Waits { get; }
        public TimeSpan Budget { get; } = TimeSpan.FromSeconds(1);
        public Task Entered => _entered.Task;
        public Task PhysicalCompletion => _completed.Task;

        public void Acknowledge()
        {
            Volatile.Write(ref _frontier, 1);
            Waits.Notify();
        }

        public void Compete(Competitor competitor, CancellationTokenSource caller)
        {
            switch (competitor)
            {
                case Competitor.Shutdown:
                    Waits.StopForShutdown();
                    break;
                case Competitor.Deadline:
                    _time.Advance(Budget);
                    break;
                default:
                    caller.Cancel();
                    break;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _release.TrySetResult();
            await _completed.Task.WaitAsync(TestTimeouts.Expected);
        }
    }
}
