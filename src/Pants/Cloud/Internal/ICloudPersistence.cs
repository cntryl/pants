namespace Cntryl.Pants.Cloud.Internal;

interface ICloudPersistence : ICloudDdlAuthority
{
    bool HasPersistenceAnomaly { get; }

    ValueTask PublishWalBatchAsync(
        IReadOnlyList<SealedWalSegment> segments,
        CancellationToken cancellationToken);

    ValueTask MirrorMetadataAndSstsAsync(CancellationToken cancellationToken);

    ValueTask CollectObsoleteSstsAsync(CancellationToken cancellationToken);

    ValueTask ValidateWriteAuthorityAsync(CancellationToken cancellationToken);

    ValueTask<ReadOnlyMemory<byte>?> FetchSstAsync(
        string name,
        CancellationToken cancellationToken);
}
