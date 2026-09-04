namespace Cntryl.Pants.Cloud;

public interface IPantsCloudObjectStore : IAsyncDisposable
{
    ValueTask IAsyncDisposable.DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    ValueTask<PantsCloudObject?> GetAsync(
        string objectKey,
        CancellationToken cancellationToken = default);

    async ValueTask<PantsCloudObject?> GetRangeAsync(
        string objectKey,
        ulong offset,
        int length,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var value = await GetAsync(objectKey, cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            return null;
        }

        if (offset > (ulong)value.Data.Length || (ulong)length > (ulong)value.Data.Length - offset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(length),
                "The requested range is outside the cloud object.");
        }

        return new PantsCloudObject(
            value.Data.Slice(checked((int)offset), length).ToArray(),
            value.Version);
    }

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
