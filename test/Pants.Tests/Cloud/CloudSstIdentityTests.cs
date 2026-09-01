namespace Cntryl.Pants.Tests.Cloud;

public sealed class CloudSstIdentityTests
{
    [Theory]
    [InlineData("sst/../000000_01_00000000000000000001.sst")]
    [InlineData("sst/nested/000000_01_00000000000000000001.sst")]
    [InlineData("sst/000000_01_00000000000000000001.sst\\escape")]
    [InlineData("sst/C:000000_01_00000000000000000001.sst")]
    public void ShouldRejectPathUnsafeObjectKey(string objectKey)
    {
        // Direct coverage motivated by issue #99's malformed-key GC poison.
        Assert.False(CloudSstObjectKey.TryGetName(objectKey, out _));
    }

    [Theory]
    [InlineData("0_01_00000000000000000001.sst")]
    [InlineData("000000_1_00000000000000000001.sst")]
    [InlineData("000000_01_1.sst")]
    [InlineData("000000_01_00000000000000000001_extra.sst")]
    [InlineData("000000_01_00000000000000000001.txt")]
    public void ShouldRejectNonCanonicalSstIdentity(string name)
    {
        Assert.False(CloudSstIdentity.TryParse(name, out _));
    }

    [Fact]
    public void ShouldParseCanonicalSstObjectIdentity()
    {
        const string name = "000007_03_00000000000000000042.sst";

        Assert.True(CloudSstObjectKey.TryGetName("sst/" + name, out var parsedName));
        Assert.True(CloudSstIdentity.TryParse(parsedName, out var identity));
        Assert.Equal(7u, identity.ColumnFamilyId);
        Assert.Equal(42UL, identity.Sequence);
    }
}
