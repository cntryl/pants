namespace Cntryl.Pants.Contracts;

public sealed class PantsCloudObjectContractTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void ShouldRejectMissingReadIdentity(string? version)
    {
        Assert.Throws<PantsIOException>(() => new PantsCloudObject("abc"u8.ToArray(), version!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void ShouldRejectMissingReadIdentityInRecordCopies(string? version)
    {
        var original = new PantsCloudObject("abc"u8.ToArray(), "original");

        Assert.Throws<PantsIOException>(() => (original with { Version = version! }).Version);
        Assert.Equal("original", original.Version);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" \t", null)]
    [InlineData("etag", "")]
    [InlineData("etag", " \t")]
    public void ShouldRejectMissingMetadataIdentity(string? etag, string? generation)
    {
        Assert.Throws<PantsIOException>(() => new PantsCloudObjectMetadata(3, etag!, generation, null));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" \t", null)]
    [InlineData("etag", "")]
    [InlineData("etag", " \t")]
    public void ShouldRejectMissingMetadataIdentityWhenConsumingRecordCopies(string? etag, string? generation)
    {
        var original = new PantsCloudObjectMetadata(3, "original", null, null);

        Assert.Throws<PantsIOException>(() => (original with { ETag = etag!, Generation = generation }).Version);
        Assert.Equal("original", original.Version);
    }

    [Theory]
    [InlineData("etag", null, "etag")]
    [InlineData("etag", "42", "42")]
    [InlineData("", "42", "42")]
    public void ShouldPreserveTheProviderConditionalIdentity(string etag, string? generation, string expected)
    {
        var metadata = new PantsCloudObjectMetadata(3, etag, generation, null);
        var value = new PantsCloudObject("abc"u8.ToArray(), metadata.Version);
        var (data, version) = value;

        Assert.Equal(expected, metadata.Version);
        Assert.Equal(expected, version);
        Assert.Equal("abc"u8.ToArray(), data.ToArray());
        Assert.Equal(value, value with { });
    }
}
