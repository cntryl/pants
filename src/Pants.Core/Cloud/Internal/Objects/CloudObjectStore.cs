namespace Cntryl.Pants.Cloud.Internal.Objects;

abstract class CloudObjectStore : ICloudObjectStore
{
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    public abstract ValueTask<CloudObject?> GetAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    public abstract ValueTask<CloudObject?> GetRangeAsync(
        string objectKey,
        ulong offset,
        int length,
        CancellationToken cancellationToken = default);

    public abstract ValueTask<CloudObjectMetadata?> HeadAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    public abstract ValueTask<bool> PutAsync(
        string objectKey,
        ReadOnlyMemory<byte> data,
        CloudObjectWriteCondition condition,
        CancellationToken cancellationToken = default);

    public abstract ValueTask<CloudObjectListPage> ListPageAsync(
        string prefix,
        string? continuationToken,
        CancellationToken cancellationToken = default);

    public abstract ValueTask<CloudObjectDeleteOutcome> DeleteAsync(
        string objectKey,
        CloudObjectDeleteCondition condition,
        CancellationToken cancellationToken = default);
}
