namespace Pants;

internal interface ICloudObjectStore
{
    ValueTask<CloudObject?> GetAsync(
        string objectKey,
        CancellationToken cancellationToken);

    ValueTask<bool> PutAsync(
        string objectKey,
        ReadOnlyMemory<byte> data,
        CloudObjectWriteCondition condition,
        CancellationToken cancellationToken);
}
