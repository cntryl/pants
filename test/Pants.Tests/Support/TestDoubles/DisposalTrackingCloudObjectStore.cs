namespace Cntryl.Pants.Support.TestDoubles;

sealed class DisposalTrackingCloudObjectStore(Exception? disposalFailure = null) : ICloudObjectStore
{
    readonly TestCloudObjectStore _inner = new();
    int _disposeCount;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public ValueTask<CloudObject?> GetAsync(string objectKey, CancellationToken cancellationToken) =>
        _inner.GetAsync(objectKey, cancellationToken);

    public ValueTask<CloudObjectMetadata?> HeadAsync(string objectKey, CancellationToken cancellationToken) =>
        _inner.HeadAsync(objectKey, cancellationToken);

    public ValueTask<bool> PutAsync(
        string objectKey,
        ReadOnlyMemory<byte> data,
        CloudObjectWriteCondition condition,
        CancellationToken cancellationToken) =>
        _inner.PutAsync(objectKey, data, condition, cancellationToken);

    public ValueTask<CloudObjectListPage> ListPageAsync(
        string prefix,
        string? continuationToken,
        CancellationToken cancellationToken) =>
        _inner.ListPageAsync(prefix, continuationToken, cancellationToken);

    public ValueTask<CloudObjectDeleteOutcome> DeleteAsync(
        string objectKey,
        CloudObjectDeleteCondition condition,
        CancellationToken cancellationToken) =>
        _inner.DeleteAsync(objectKey, condition, cancellationToken);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        GC.SuppressFinalize(this);
        return disposalFailure is null
            ? ValueTask.CompletedTask
            : ValueTask.FromException(disposalFailure);
    }
}
