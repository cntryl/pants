namespace Cntryl.Pants.Cloud;

public sealed class ImmutablePublicationEnvelopeTests
{
    [Fact]
    public void ShouldLeaveThreeQuartersOfMaintenancePoolForLiveCompactionInputs()
    {
        const long pool = 32L * 1024 * 1024;
        const long variable = pool - ImmutablePublicationEnvelope.FixedWorkspaceBytes;

        var partition = ImmutablePublicationEnvelope.PartitionTarget(pool);

        Assert.Equal(variable / (ImmutablePublicationEnvelope.CopyFactor * 4), partition);
        Assert.True(partition * ImmutablePublicationEnvelope.CopyFactor <= variable / 4);
        Assert.True(ImmutablePublicationEnvelope.For(partition) <= pool);
    }

    [Fact]
    public void ShouldChargeUploadCopiesPlusFixedReadbackWorkspace()
    {
        var envelope = ImmutablePublicationEnvelope.For(1000);

        Assert.Equal(
            1000L * ImmutablePublicationEnvelope.CopyFactor + ImmutablePublicationEnvelope.FixedWorkspaceBytes,
            envelope);
    }

    [Fact]
    public void ShouldKeepAPositivePartitionTargetForAPoolSmallerThanTheFixedWorkspace()
    {
        Assert.Equal(1, ImmutablePublicationEnvelope.PartitionTarget(1024));
    }
}
