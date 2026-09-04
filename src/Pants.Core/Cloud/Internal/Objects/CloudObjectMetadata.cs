namespace Cntryl.Pants.Cloud.Internal.Objects;

sealed record CloudObjectMetadata(
    ulong SizeBytes,
    string ETag,
    string? Generation,
    DateTimeOffset? LastModifiedUtc)
{
    public string Version => Generation ?? ETag;
}
