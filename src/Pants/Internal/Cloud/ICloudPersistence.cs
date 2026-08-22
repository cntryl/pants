namespace Pants;

interface ICloudPersistence
{
    ValueTask PublishWalAsync(
        SealedWalSegment segment,
        CancellationToken cancellationToken);

    ValueTask MirrorMetadataAndSstsAsync(CancellationToken cancellationToken);

    ValueTask PublishColumnFamilyCreateAsync(
        MidgeColumnFamilyMeta metadata,
        CancellationToken cancellationToken);

    ValueTask PublishColumnFamilyDropAsync(
        MidgeColumnFamilyMeta metadata,
        CancellationToken cancellationToken);
}
