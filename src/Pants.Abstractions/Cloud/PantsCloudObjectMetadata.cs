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
        _ = Version;
    }

    public ulong SizeBytes { get; }

    public string ETag { get; init; }

    public string? Generation { get; init; }

    public DateTimeOffset? LastModifiedUtc { get; }

    /// <summary>
    ///     The non-empty conditional identity: generation when supplied, otherwise ETag.
    ///     Invalid metadata, including modified record copies, fails closed when consumed.
    /// </summary>
    public string Version => CloudObjectIdentity.RequireVersion(Generation ?? ETag);
}
