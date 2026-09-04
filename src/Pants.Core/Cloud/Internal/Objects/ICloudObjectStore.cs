namespace Cntryl.Pants.Cloud.Internal.Objects;

interface ICloudObjectStore
{
    ValueTask<CloudObject?> GetAsync(
        string objectKey,
        CancellationToken cancellationToken);

    async ValueTask<CloudObject?> GetRangeAsync(
        string objectKey,
        ulong offset,
        int length,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var value = await GetAsync(objectKey, cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            return null;
        }

        if (offset > (ulong)value.Data.Length || (ulong)length > (ulong)value.Data.Length - offset)
        {
            throw new PantsIOException("The requested cloud object range is outside the object.");
        }

        return new CloudObject(
            value.Data.Slice(checked((int)offset), length).ToArray(),
            value.Version);
    }

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
