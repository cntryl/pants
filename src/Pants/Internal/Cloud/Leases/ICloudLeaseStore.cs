namespace Pants;

internal interface ICloudLeaseStore
{
    ValueTask<CloudLeaseSnapshot?> ReadAsync(CancellationToken cancellationToken);

    ValueTask<bool> TryCreateAsync(
        CloudLeaseRecord lease,
        CancellationToken cancellationToken);

    ValueTask<bool> TryReplaceAsync(
        string expectedVersion,
        CloudLeaseRecord lease,
        CancellationToken cancellationToken);
}
