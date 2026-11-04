using System.Collections.Immutable;

namespace Cntryl.Pants.Storage.Internal.Accounting;

sealed record MetadataAccountingBucket(
    MetadataOrigin Origin,
    MetadataMedium Medium,
    ImmutableArray<ulong> Counters,
    ImmutableArray<ulong> CheckpointLatency,
    ImmutableArray<ulong> PublicationLatency,
    ImmutableArray<ulong> CommittedSstSizes,
    ulong ActiveOperations)
{
    public ulong this[MetadataCounter counter] => Counters[(int)counter];
}
