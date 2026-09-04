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
    public void ShouldExposeExplicitUnboundedOwnershipForCallerlessObligations()
    {
        var deadline = OperationDeadline.Unbounded;

        Assert.False(deadline.IsBounded);
        Assert.False(deadline.IsExpired);
        Assert.Equal(TimeSpan.MaxValue, deadline.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(7), deadline.Clamp(TimeSpan.FromSeconds(7)));
    }
}
