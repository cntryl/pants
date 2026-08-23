namespace Pants;

internal sealed record CloudObjectMetadata(
    ulong SizeBytes,
    string ETag,
    string? Generation,
    DateTimeOffset? LastModifiedUtc)
{
    public string Version => Generation ?? ETag;
}
