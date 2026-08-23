namespace Cntryl.Pants.Tests.Support.TestDoubles;

sealed class CountingCloudObjectStore : ICloudObjectStore
{
    readonly Dictionary<string, (byte[] Data, string Version)> _objects = new(StringComparer.Ordinal);
    int _getCount;
    int _putCount;

    public int GetCount => Volatile.Read(ref _getCount);

    public int PutCount => Volatile.Read(ref _putCount);

    public long PayloadBytesCopied { get; private set; }

    public ValueTask<CloudObject?> GetAsync(string objectKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _getCount);
        return ValueTask.FromResult(_objects.TryGetValue(objectKey, out var value)
            ? new CloudObject(value.Data, value.Version)
            : null);
    }

    public ValueTask<CloudObjectMetadata?> HeadAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_objects.TryGetValue(objectKey, out var value)
            ? new CloudObjectMetadata(checked((ulong)value.Data.Length), value.Version, null, null)
            : null);
    }

    public ValueTask<bool> PutAsync(
        string objectKey,
        ReadOnlyMemory<byte> data,
        CloudObjectWriteCondition condition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _putCount);
        var exists = _objects.TryGetValue(objectKey, out var current);
        var accepted = condition switch
        {
            CloudObjectWriteCondition.Unconditional => true,
            CloudObjectWriteCondition.IfAbsent => !exists,
            CloudObjectWriteCondition.IfVersion expected =>
                exists && StringComparer.Ordinal.Equals(expected.Version, current.Version),
            _ => false
        };
        if (!accepted)
        {
            return ValueTask.FromResult(false);
        }

        var copy = data.ToArray();
        PayloadBytesCopied += copy.Length;
        _objects[objectKey] = (copy, Guid.NewGuid().ToString("N"));
        return ValueTask.FromResult(true);
    }

    public ValueTask<CloudObjectListPage> ListPageAsync(
        string prefix,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var keys = continuationToken is null
            ? _objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray()
            : [];
        return ValueTask.FromResult(new CloudObjectListPage(keys, null));
    }

    public ValueTask<CloudObjectDeleteOutcome> DeleteAsync(
        string objectKey,
        CloudObjectDeleteCondition condition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_objects.TryGetValue(objectKey, out var current))
        {
            return ValueTask.FromResult(CloudObjectDeleteOutcome.NotFound);
        }

        if (condition is CloudObjectDeleteCondition.IfVersion expected &&
            !StringComparer.Ordinal.Equals(expected.Version, current.Version))
        {
            return ValueTask.FromResult(CloudObjectDeleteOutcome.ConditionNotMet);
        }

        _objects.Remove(objectKey);
        return ValueTask.FromResult(CloudObjectDeleteOutcome.Deleted);
    }
}
