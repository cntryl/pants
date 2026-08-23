namespace Cntryl.Pants;

internal interface ICloudObjectStore
{
    ValueTask<CloudObject?> GetAsync(
        string objectKey,
        CancellationToken cancellationToken);

    ValueTask<CloudObjectMetadata?> HeadAsync(
        string objectKey,
        CancellationToken cancellationToken);

    ValueTask<bool> PutAsync(
        string objectKey,
        ReadOnlyMemory<byte> data,
        CloudObjectWriteCondition condition,
        CancellationToken cancellationToken);

    ValueTask<CloudObjectListPage> ListPageAsync(
        string prefix,
        string? continuationToken,
        CancellationToken cancellationToken);

    ValueTask<CloudObjectDeleteOutcome> DeleteAsync(
        string objectKey,
        CloudObjectDeleteCondition condition,
        CancellationToken cancellationToken);
}
