namespace Cntryl.Pants.Tests.Runtime;

public sealed class OperationDeadlineTests
{
    [Fact]
    public void ShouldChargeElapsedQueueTimeAndClampNestedOperations()
    {
        var time = new ManualTimeProvider();
        var started = time.GetTimestamp();
        var deadline = OperationDeadline.FromStart(
            started,
            TimeSpan.FromMilliseconds(100),
            time);
        time.Advance(TimeSpan.FromMilliseconds(75));

        Assert.Equal(TimeSpan.FromMilliseconds(25), deadline.Remaining);
        Assert.Equal(TimeSpan.FromMilliseconds(10), deadline.Clamp(TimeSpan.FromMilliseconds(10)));
        Assert.Equal(TimeSpan.FromMilliseconds(25), deadline.Clamp(TimeSpan.FromSeconds(1)));
        Assert.False(deadline.IsExpired);

        time.Advance(TimeSpan.FromMilliseconds(25));
        Assert.True(deadline.IsExpired);
        Assert.Equal(TimeSpan.Zero, deadline.Remaining);
    }

    [Fact]
    public async Task ShouldRejectExpiredWorkBeforeSubmission()
    {
        var time = new ManualTimeProvider();
        var deadline = OperationDeadline.FromBudget(TimeSpan.Zero, time);
        var calls = 0;

        var exception = await Assert.ThrowsAsync<PantsTimeoutException>(() => deadline.RunAsync(
            _ =>
            {
                calls++;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None).AsTask());

        Assert.Equal(0, calls);
        Assert.Contains("before submission", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShouldPerformZeroCloudCallsGivenDeadlineExpiredBeforeSubmission()
    {
        var store = new TestCloudObjectStore();
        var deadline = OperationDeadline.FromBudget(TimeSpan.Zero, new ManualTimeProvider());

        var exception = await Assert.ThrowsAsync<PantsTimeoutException>(() => store.PutAsync(
            "private/object-key",
            "private-value"u8.ToArray(),
            new CloudObjectWriteCondition.IfAbsent(),
            deadline,
            CancellationToken.None).AsTask());

        Assert.Equal(0, store.PutCount);
        Assert.Contains("before submission", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private/object-key", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldConsumeOneBudgetAcrossSequentialCloudCalls()
    {
        var time = new ManualTimeProvider();
        var deadline = OperationDeadline.FromBudget(TimeSpan.FromMilliseconds(100), time);
        var firstCalls = 0;
        var secondCalls = 0;

        await deadline.RunAsync(
            _ =>
            {
                firstCalls++;
                time.Advance(TimeSpan.FromMilliseconds(100));
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);
        _ = await Assert.ThrowsAsync<PantsTimeoutException>(() => deadline.RunAsync(
            _ =>
            {
                secondCalls++;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None).AsTask());

        Assert.Equal(1, firstCalls);
        Assert.Equal(0, secondCalls);
    }

    [Fact]
    public async Task ShouldClassifyMutationTimeoutAsOutcomeIndeterminate()
    {
        var store = new TestCloudObjectStore
        {
            BeforeNextPutAsync = async cancellationToken =>
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
        };
        var deadline = OperationDeadline.FromBudget(TimeSpan.FromMilliseconds(20));

        var exception = await Assert.ThrowsAsync<PantsIOException>(() => store.PutAsync(
            "object-key",
            "value"u8.ToArray(),
            new CloudObjectWriteCondition.IfAbsent(),
            deadline,
            CancellationToken.None).AsTask());

        Assert.Equal(1, store.PutCount);
        Assert.Contains("indeterminate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldExposeExplicitUnboundedOwnershipForCallerlessObligations()
    {
        var deadline = OperationDeadline.Unbounded;

        Assert.False(deadline.IsBounded);
        Assert.False(deadline.IsExpired);
        Assert.Equal(TimeSpan.MaxValue, deadline.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(7), deadline.Clamp(TimeSpan.FromSeconds(7)));
    }
}
