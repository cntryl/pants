namespace Pants;

interface ICloudPersistence : ICloudDdlAuthority
{
    bool HasPersistenceAnomaly { get; }

    ValueTask PublishWalAsync(
        SealedWalSegment segment,
        CancellationToken cancellationToken);

    ValueTask MirrorMetadataAndSstsAsync(CancellationToken cancellationToken);

    ValueTask CollectObsoleteSstsAsync(CancellationToken cancellationToken);

    ValueTask ValidateWriteAuthorityAsync(CancellationToken cancellationToken);

    ValueTask<ReadOnlyMemory<byte>?> FetchSstAsync(
        string name,
        CancellationToken cancellationToken);
}
