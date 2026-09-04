namespace Cntryl.Pants.Cloud;

public sealed class CloudWalUploadTrackerTests
{
    [Fact]
    public void ShouldCountEachSealedSegmentUntilItsPublicationCompletes()
    {
        var telemetry = new RuntimeTelemetry();
        var tracker = new CloudWalUploadTracker(telemetry);
        var segment = new SealedWalSegment(7, 3, 11, "7.wal", [1, 2, 3]);

        Assert.True(tracker.Admit(segment));
        Assert.False(tracker.Admit(segment));
        Assert.Equal(1, tracker.Count);
        Assert.Equal(1, telemetry.PendingCloudUploads);

        Assert.True(tracker.Complete(segment));
        Assert.False(tracker.Complete(segment));
        Assert.Equal(0, tracker.Count);
        Assert.Equal(0, telemetry.PendingCloudUploads);
    }
}
