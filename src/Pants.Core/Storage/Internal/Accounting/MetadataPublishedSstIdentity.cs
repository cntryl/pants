namespace Cntryl.Pants.Storage.Internal.Accounting;

readonly record struct MetadataPublishedSstIdentity(
    string Name,
    uint FamilyId,
    ulong SstSequence,
    ulong Bytes,
    uint? ContentCrc32C);
