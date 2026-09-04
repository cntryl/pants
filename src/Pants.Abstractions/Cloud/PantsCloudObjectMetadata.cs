namespace Cntryl.Pants.Cloud;

public sealed record PantsCloudObjectMetadata
{
    public PantsCloudObjectMetadata(
        ulong sizeBytes,
        string version,
        DateTimeOffset? lastModifiedUtc) : this(
        sizeBytes,
        version,
        null,
        lastModifiedUtc)
    {
    }

    public PantsCloudObjectMetadata(
        ulong sizeBytes,
        string eTag,
        string? generation,
        DateTimeOffset? lastModifiedUtc)
    {
        SizeBytes = sizeBytes;
        ETag = eTag;
        Generation = generation;
        LastModifiedUtc = lastModifiedUtc;
    }

    public ulong SizeBytes { get; }

    public string ETag { get; init; }

    public string? Generation { get; init; }

    public DateTimeOffset? LastModifiedUtc { get; }

    public string Version => Generation ?? ETag;
}
