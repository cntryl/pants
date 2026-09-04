using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudWalSealControllerTests
{
    [Fact]
    public void ShouldKeepEmptyWalUnsealedGivenElapsedMaximumDelay()
    {
        var timeProvider = new ManualTimeProvider();
        var controller = new CloudWalSealController(CreatePolicy(), timeProvider);
        timeProvider.Advance(TimeSpan.FromMinutes(1));

        Assert.False(controller.ShouldSeal(0));
    }

    [Fact]
    public void ShouldSealCloudAsyncWalGivenMinimumSegmentBytes()
    {
        var controller = new CloudWalSealController(
            CreatePolicy(),
            new ManualTimeProvider());
        controller.RecordWrite();

        Assert.True(controller.ShouldSeal(1024));
    }

    [Fact]
    public void ShouldSealCloudAsyncWalGivenMaximumPendingWrites()
    {
        var controller = new CloudWalSealController(
            CreatePolicy(),
            new ManualTimeProvider());
        controller.RecordWrite();
        controller.RecordWrite();

        Assert.True(controller.ShouldSeal(1));
    }

    [Fact]
    public void ShouldCountEveryPhysicalRecordGivenSpilledTransaction()
    {
        var controller = new CloudWalSealController(
            CreatePolicy(),
            new ManualTimeProvider());

        controller.RecordWrite(8);

        Assert.Equal(8, controller.PendingWrites);
        Assert.True(controller.ShouldSeal(1));
    }

    [Fact]
    public void ShouldExposeRemainingDelayGivenSubthresholdCloudAsyncWal()
    {
        var timeProvider = new ManualTimeProvider();
        var controller = new CloudWalSealController(CreatePolicy(), timeProvider);
        controller.RecordWrite();
        timeProvider.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromSeconds(2), controller.RemainingDelay);
    }

    [Fact]
    public void ShouldResetPendingWritesAndDelayGivenSuccessfulSeal()
    {
        var timeProvider = new ManualTimeProvider();
        var controller = new CloudWalSealController(CreatePolicy(), timeProvider);
        controller.RecordWrite();
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        controller.RecordSeal();

        Assert.Equal(0, controller.PendingWrites);
        Assert.Null(controller.RemainingDelay);
        Assert.False(controller.ShouldSeal(1024));
    }

    static PantsCloudWritePolicy CreatePolicy() => new(
        4,
        1024,
        TimeSpan.FromSeconds(5),
        2);
}
