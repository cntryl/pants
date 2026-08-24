namespace Cntryl.Pants.Tests.Storage;

public sealed class PantsSstBlockCoverageTests
{
    [Fact]
    public void ShouldRejectUnreferencedBytesBetweenSstBlocks()
    {
        var exception = Assert.Throws<StorageException>(() => Validate(
            footerOffset: 21,
            metadata: new SstBlockHandle(0, 10),
            index: new SstBlockHandle(11, 10)));

        Assert.Equal("SST block references leave unreferenced bytes.", exception.Message);
    }

    [Fact]
    public void ShouldRejectOverlappingSstBlocks()
    {
        var exception = Assert.Throws<StorageException>(() => Validate(
            footerOffset: 19,
            metadata: new SstBlockHandle(0, 10),
            index: new SstBlockHandle(9, 10)));

        Assert.Equal("SST block references overlap.", exception.Message);
    }

    [Fact]
    public void ShouldRejectSstBlocksThatDoNotExactlyReachFooter()
    {
        var exception = Assert.Throws<StorageException>(() => Validate(
            footerOffset: 20,
            metadata: new SstBlockHandle(0, 10),
            index: new SstBlockHandle(10, 9)));

        Assert.Equal("SST block references do not exactly reach the footer.", exception.Message);
    }

    static void Validate(
        long footerOffset,
        SstBlockHandle metadata,
        SstBlockHandle index) =>
        SstCodec.ValidateBlockCoverage(
            footerOffset,
            metadata,
            index,
            trie: null,
            bloom: null,
            range: null,
            []);
}
