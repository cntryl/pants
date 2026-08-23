namespace Cntryl.Pants.Tests;

sealed class SnapshotConsistencyCloudObjectStore : ICloudObjectStore
{
    readonly Lock _gate = new();
    readonly Dictionary<string, (byte[] Data, long Version)> _objects =
        new(StringComparer.Ordinal);
    int _manifestSnapshotGetCount;

    public Func<string, int, CancellationToken, ValueTask>? BeforeGetAsync { get; set; }

    public void Seed(string objectKey, ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            _objects[objectKey] = (data.ToArray(), 1);
        }
    }

    public byte[]? GetData(string objectKey)
    {
        lock (_gate)
        {
            return _objects.TryGetValue(objectKey, out var value)
                ? value.Data.ToArray()
                : null;
        }
    }

    public async ValueTask<CloudObject?> GetAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = objectKey.EndsWith(
            "/manifest.snapshot.json",
            StringComparison.Ordinal)
            ? Interlocked.Increment(ref _manifestSnapshotGetCount)
            : 0;
        if (BeforeGetAsync is { } beforeGet)
        {
            await beforeGet(objectKey, count, cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            return _objects.TryGetValue(objectKey, out var value)
                ? new CloudObject(value.Data.ToArray(), FormatVersion(value.Version))
                : null;
        }
    }

    public ValueTask<CloudObjectMetadata?> HeadAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult<CloudObjectMetadata?>(
                _objects.TryGetValue(objectKey, out var value)
                    ? new CloudObjectMetadata(
                        checked((ulong)value.Data.Length),
                        FormatVersion(value.Version),
                        Generation: null,
                        LastModifiedUtc: null)
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
        lock (_gate)
        {
            var exists = _objects.TryGetValue(objectKey, out var current);
            var accepted = condition switch
            {
                CloudObjectWriteCondition.Unconditional => true,
                CloudObjectWriteCondition.IfAbsent => !exists,
                CloudObjectWriteCondition.IfVersion expected =>
                    exists && StringComparer.Ordinal.Equals(
                        expected.Version,
                        FormatVersion(current.Version)),
                _ => false
            };
            if (!accepted)
            {
                return ValueTask.FromResult(false);
            }

            _objects[objectKey] = (
                data.ToArray(),
                exists ? checked(current.Version + 1) : 1);
            return ValueTask.FromResult(true);
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
                ? _objects.Keys
                    .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal)
                    .ToArray()
                : [];
            return ValueTask.FromResult(new CloudObjectListPage(keys, continuationToken: null));
        }
    }

    public ValueTask<CloudObjectDeleteOutcome> DeleteAsync(
        string objectKey,
        CloudObjectDeleteCondition condition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_objects.TryGetValue(objectKey, out var current))
            {
                return ValueTask.FromResult(CloudObjectDeleteOutcome.NotFound);
            }

            if (condition is CloudObjectDeleteCondition.IfVersion expected &&
                !StringComparer.Ordinal.Equals(
                    expected.Version,
                    FormatVersion(current.Version)))
            {
                return ValueTask.FromResult(CloudObjectDeleteOutcome.ConditionNotMet);
            }

            _objects.Remove(objectKey);
            return ValueTask.FromResult(CloudObjectDeleteOutcome.Deleted);
        }
    }

    static string FormatVersion(long version) =>
        version.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
