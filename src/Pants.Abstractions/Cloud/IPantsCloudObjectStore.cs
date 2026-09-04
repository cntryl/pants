namespace Cntryl.Pants.Cloud;

public interface IPantsCloudObjectStore : IAsyncDisposable
{
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Reads bytes and their non-empty conditional version from one object observation.
    ///     Do not combine a GET body with identity from an independent HEAD, even if lengths match.
    ///     Returns null only when the object is absent; invalid responses must throw.
    /// </summary>
    ValueTask<PantsCloudObject?> GetAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads a range and its version from the same object observation. The version uses the
    ///     same identity as GET, HEAD, and conditional mutations (generation for GCS).
    ///     Implementations must bound transfer and buffering by the requested range, not object
    ///     size. Providers without ranged reads fail explicitly; there is no full-GET fallback.
    /// </summary>
    ValueTask<PantsCloudObject?> GetRangeAsync(
        string objectKey,
        ulong offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromException<PantsCloudObject?>(new PantsNotSupportedException(
            "This cloud object store does not support bounded ranged reads. Implement GetRangeAsync to use this capability."));
    }

    /// <summary>
    ///     Reads metadata with a non-empty conditional identity. Independent metadata and body
    ///     observations must not be treated as one version-bound read.
    /// </summary>
    ValueTask<PantsCloudObjectMetadata?> HeadAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    ValueTask<bool> PutAsync(
        string objectKey,
        ReadOnlyMemory<byte> data,
        PantsCloudObjectWriteCondition condition,
        CancellationToken cancellationToken = default);

    ValueTask<PantsCloudObjectListPage> ListPageAsync(
        string prefix,
        string? continuationToken,
        CancellationToken cancellationToken = default);

    ValueTask<PantsCloudObjectDeleteOutcome> DeleteAsync(
        string objectKey,
        PantsCloudObjectDeleteCondition condition,
        CancellationToken cancellationToken = default);
}
