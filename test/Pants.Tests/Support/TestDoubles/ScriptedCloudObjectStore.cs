using System.Collections.Concurrent;

namespace Cntryl.Pants.Support.TestDoubles;

/// <summary>
///     A thread-safe in-memory object store with conditional writes, ranged reads, a record of every
///     request, and a hook that runs before each ranged read (to slow it, fail it, or advance a clock).
/// </summary>
sealed class ScriptedCloudObjectStore : ICloudObjectStore
{
    readonly Lock _gate = new();
    readonly Dictionary<string, (byte[] Data, string Version)> _objects = new(StringComparer.Ordinal);
    readonly ConcurrentQueue<StoreRequest> _requests = new();
    long _versions;

    public bool SupportsRanges { get; init; } = true;

    /// <summary>Runs before a ranged read reaches the objects; it may throw or wait.</summary>
    public Func<string, CancellationToken, ValueTask>? BeforeRangeRead { get; set; }

    public IReadOnlyCollection<StoreRequest> Requests => _requests;

    public void ResetRequests() => _requests.Clear();

    public IEnumerable<StoreRequest> RequestsFor(string objectKey) =>
        _requests.Where(request => StringComparer.Ordinal.Equals(request.ObjectKey, objectKey));

    public long RangeBytesRead(string objectKey) =>
        RequestsFor(objectKey)
            .Where(static request => request.Operation == StoreOperation.GetRange)
            .Sum(static request => (long)request.Length);

    /// <summary>The start offset of every ranged read of an object, in request order.</summary>
    public ulong[] RangeOffsets(string objectKey) =>
        RequestsFor(objectKey)
            .Where(static request => request.Operation == StoreOperation.GetRange)
            .Select(static request => request.Offset)
            .ToArray();

    public bool Contains(string objectKey)
    {
        lock (_gate)
        {
            return _objects.ContainsKey(objectKey);
        }
    }

    public byte[] Read(string objectKey)
    {
        lock (_gate)
        {
            return _objects[objectKey].Data.ToArray();
        }
    }

    /// <summary>Writes an object unconditionally, giving it a new version.</summary>
    public void Write(string objectKey, byte[] data)
    {
        lock (_gate)
        {
            _objects[objectKey] = (data.ToArray(), NextVersion());
        }
    }

    public ValueTask<CloudObject?> GetAsync(string objectKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _requests.Enqueue(new StoreRequest(StoreOperation.Get, objectKey, 0, 0));
        lock (_gate)
        {
            return ValueTask.FromResult(_objects.TryGetValue(objectKey, out var value)
                ? new CloudObject(value.Data.ToArray(), value.Version)
                : null);
        }
    }

    public async ValueTask<CloudObject?> GetRangeAsync(
        string objectKey,
        ulong offset,
        int length,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SupportsRanges)
        {
            throw new PantsNotSupportedException("This test store has no ranged reads.");
        }

        if (BeforeRangeRead is { } before)
        {
            await before(objectKey, cancellationToken).ConfigureAwait(false);
        }

        _requests.Enqueue(new StoreRequest(StoreOperation.GetRange, objectKey, offset, length));
        lock (_gate)
        {
            if (!_objects.TryGetValue(objectKey, out var value))
            {
                return null;
            }

            var start = checked((int)offset);
            var count = Math.Min(length, value.Data.Length - start);
            return new CloudObject(value.Data.AsSpan(start, count).ToArray(), value.Version);
        }
    }

    public ValueTask<CloudObjectMetadata?> HeadAsync(string objectKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _requests.Enqueue(new StoreRequest(StoreOperation.Head, objectKey, 0, 0));
        lock (_gate)
        {
            return ValueTask.FromResult(_objects.TryGetValue(objectKey, out var value)
                ? new CloudObjectMetadata(checked((ulong)value.Data.Length), value.Version, null, null)
                : null);
        }
    }

    public ValueTask<bool> PutAsync(
        string objectKey,
        ReadOnlyMemory<byte> data,
        CloudObjectWriteCondition condition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _requests.Enqueue(new StoreRequest(StoreOperation.Put, objectKey, 0, data.Length));
        lock (_gate)
        {
            var exists = _objects.TryGetValue(objectKey, out var current);
            var accepted = condition switch
            {
                PantsCloudObjectWriteCondition.Unconditional => true,
                PantsCloudObjectWriteCondition.IfAbsent => !exists,
                PantsCloudObjectWriteCondition.IfVersion expected =>
                    exists && StringComparer.Ordinal.Equals(expected.Version, current.Version),
                _ => false
            };
            if (accepted)
            {
                _objects[objectKey] = (data.ToArray(), NextVersion());
            }

            return ValueTask.FromResult(accepted);
        }
    }

    public ValueTask<CloudObjectListPage> ListPageAsync(
        string prefix,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var keys = continuationToken is null
                ? _objects.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray()
                : [];
            return ValueTask.FromResult(new CloudObjectListPage(keys, null));
        }
    }

    public ValueTask<CloudObjectDeleteOutcome> DeleteAsync(
        string objectKey,
        CloudObjectDeleteCondition condition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _requests.Enqueue(new StoreRequest(StoreOperation.Delete, objectKey, 0, 0));
        lock (_gate)
        {
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

    string NextVersion() => Interlocked.Increment(ref _versions).ToString(
        System.Globalization.CultureInfo.InvariantCulture);
}
