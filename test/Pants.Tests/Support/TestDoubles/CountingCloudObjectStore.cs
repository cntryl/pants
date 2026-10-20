namespace Cntryl.Pants.Support.TestDoubles;

sealed class CountingCloudObjectStore : ICloudObjectStore
{
    readonly Dictionary<string, (byte[] Data, string Version)> _objects = new(StringComparer.Ordinal);
    int _getCount;
    int _headCount;
    int _rangeGetCount;
    int _putCount;
    int _ifAbsentPutCount;
    int _ifVersionPutCount;
    int _unconditionalPutCount;
    int _listPageCount;
    int _deleteCount;
    int _largestRangeBytes;
    long _payloadBytesCopied;

    public int GetCount => Volatile.Read(ref _getCount);

    public int HeadCount => Volatile.Read(ref _headCount);

    public int RangeGetCount => Volatile.Read(ref _rangeGetCount);

    public int LargestRangeBytes => Volatile.Read(ref _largestRangeBytes);

    public int PutCount => Volatile.Read(ref _putCount);

    public int ConditionalPutCount => checked(
        Volatile.Read(ref _ifAbsentPutCount) + Volatile.Read(ref _ifVersionPutCount));

    public int UnconditionalPutCount => Volatile.Read(ref _unconditionalPutCount);

    public int RequestCount => checked(
        HeadCount + GetCount + RangeGetCount + PutCount +
        Volatile.Read(ref _listPageCount) + Volatile.Read(ref _deleteCount));

    public long PayloadBytesCopied => Volatile.Read(ref _payloadBytesCopied);

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
        Interlocked.Increment(ref _headCount);
        return ValueTask.FromResult(_objects.TryGetValue(objectKey, out var value)
            ? new CloudObjectMetadata(checked((ulong)value.Data.Length), value.Version, null, null)
            : null);
    }

    public ValueTask<CloudObject?> GetRangeAsync(
        string objectKey,
        ulong offset,
        int length,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        Interlocked.Increment(ref _rangeGetCount);
        UpdateLargestRange(length);
        if (!_objects.TryGetValue(objectKey, out var value) || offset >= (ulong)value.Data.Length)
        {
            return ValueTask.FromResult<CloudObject?>(null);
        }

        var start = checked((int)offset);
        var count = Math.Min(length, value.Data.Length - start);
        return ValueTask.FromResult<CloudObject?>(new CloudObject(
            value.Data.AsMemory(start, count),
            value.Version));
    }

    public ValueTask<bool> PutAsync(
        string objectKey,
        ReadOnlyMemory<byte> data,
        CloudObjectWriteCondition condition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _putCount);
        switch (condition)
        {
            case PantsCloudObjectWriteCondition.Unconditional:
                Interlocked.Increment(ref _unconditionalPutCount);
                break;
            case PantsCloudObjectWriteCondition.IfAbsent:
                Interlocked.Increment(ref _ifAbsentPutCount);
                break;
            case PantsCloudObjectWriteCondition.IfVersion:
                Interlocked.Increment(ref _ifVersionPutCount);
                break;
        }

        var exists = _objects.TryGetValue(objectKey, out var current);
        var accepted = condition switch
        {
            PantsCloudObjectWriteCondition.Unconditional => true,
            PantsCloudObjectWriteCondition.IfAbsent => !exists,
            PantsCloudObjectWriteCondition.IfVersion expected =>
                exists && StringComparer.Ordinal.Equals(expected.Version, current.Version),
            _ => false
        };
        if (!accepted)
        {
            return ValueTask.FromResult(false);
        }

        var copy = data.ToArray();
        Interlocked.Add(ref _payloadBytesCopied, copy.Length);
        _objects[objectKey] = (copy, Guid.NewGuid().ToString("N"));
        return ValueTask.FromResult(true);
    }

    public ValueTask<CloudObjectListPage> ListPageAsync(
        string prefix,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _listPageCount);
        var keys = continuationToken is null
            ? _objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray()
            : [];
        return ValueTask.FromResult(new CloudObjectListPage(keys, null));
    }

    void UpdateLargestRange(int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref _largestRangeBytes);
            if (candidate <= current ||
                Interlocked.CompareExchange(ref _largestRangeBytes, candidate, current) == current)
            {
                return;
            }
        }
    }

    public ValueTask<CloudObjectDeleteOutcome> DeleteAsync(
        string objectKey,
        CloudObjectDeleteCondition condition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _deleteCount);
        if (!_objects.TryGetValue(objectKey, out var current))
        {
            return ValueTask.FromResult(CloudObjectDeleteOutcome.NotFound);
        }

        if (condition is PantsCloudObjectDeleteCondition.IfVersion expected &&
            !StringComparer.Ordinal.Equals(expected.Version, current.Version))
        {
            return ValueTask.FromResult(CloudObjectDeleteOutcome.ConditionNotMet);
        }

        _objects.Remove(objectKey);
        return ValueTask.FromResult(CloudObjectDeleteOutcome.Deleted);
    }
}
