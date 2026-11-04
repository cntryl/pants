namespace Cntryl.Pants.Storage.Internal.Accounting;

sealed record MetadataPublicationReceipt(
    string Name,
    uint FamilyId,
    ulong SstSequence,
    ulong Bytes,
    uint? ContentCrc32C,
    MetadataOrigin Origin,
    MetadataMedium Medium,
    long StartedTimestamp);
