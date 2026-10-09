namespace Cntryl.Pants.Storage;

public sealed class CompactionOutputStagingTests
{
    [Fact]
    public void ShouldSizeOutputPartitionsToHalfTheStagingWindow()
    {
        var staging = new CompactionOutputStaging(1000, static (_, _) => ValueTask.CompletedTask);

        Assert.Equal(500, staging.PartitionTargetBytes);
    }

    [Fact]
    public void ShouldRefuseAPartitionWhoseEstimateCannotFitTheStagingWindow()
    {
        var staging = new CompactionOutputStaging(1000, static (_, _) => ValueTask.CompletedTask);

        staging.AdmitPartition(1000);
        Assert.Throws<PantsNoSpaceException>(() => staging.AdmitPartition(1001));
    }

    [Fact]
    public async Task ShouldRefuseToDrainAnOutputLargerThanTheStagingWindowWithoutPublishingIt()
    {
        var drained = new List<string>();
        var staging = new CompactionOutputStaging(1000, (names, _) =>
        {
            drained.AddRange(names);
            return ValueTask.CompletedTask;
        });

        await Assert.ThrowsAsync<PantsNoSpaceException>(async () =>
            await staging.DrainAsync("a.sst", 1001, CancellationToken.None));

        Assert.Empty(drained);
    }

    [Fact]
    public async Task ShouldPublishEachOutputThatFitsTheStagingWindow()
    {
        var drained = new List<string>();
        var staging = new CompactionOutputStaging(1000, (names, _) =>
        {
            drained.AddRange(names);
            return ValueTask.CompletedTask;
        });

        await staging.DrainAsync("a.sst", 1000, CancellationToken.None);
        await staging.DrainAsync("b.sst", 10, CancellationToken.None);

        Assert.Equal(["a.sst", "b.sst"], drained);
    }
}
