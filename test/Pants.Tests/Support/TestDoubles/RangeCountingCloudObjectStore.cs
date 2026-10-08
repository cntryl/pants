namespace Cntryl.Pants.Support.TestDoubles;

sealed class RangeCountingCloudObjectStore : ICloudObjectStore
{
    readonly Dictionary<string, (byte[] Data, string Version)> _objects = new(StringComparer.Ordinal);
    int _puts;
    int _rangeGets;
    int _total;
    int _wholeGets;

    public bool ChangeVersionOnRangeRead { get; init; }

    public int Puts => _puts;

    public int RangeGets => _rangeGets;

    public int WholeGets => _wholeGets;

    public int TotalCalls => _total;

    public int LargestRangeBytes { get; private set; }

    public void ResetCounters()
    {
        _puts = _rangeGets = _wholeGets = _total = 0;
    }

    public ValueTask<CloudObject?> GetAsync(string objectKey, CancellationToken cancellationToken)
    {
        _total++;
        _wholeGets++;
        return ValueTask.FromResult(_objects.TryGetValue(objectKey, out var value)
            ? new CloudObject(value.Data, value.Version)
            : null);
    }

    public ValueTask<CloudObject?> GetRangeAsync(
        string objectKey,
        ulong offset,
        int length,
        CancellationToken cancellationToken)
    {
        _total++;
        _rangeGets++;
        LargestRangeBytes = Math.Max(LargestRangeBytes, length);
        if (!_objects.TryGetValue(objectKey, out var value))
        {
            return ValueTask.FromResult<CloudObject?>(null);
        }

        return ValueTask.FromResult<CloudObject?>(new CloudObject(
            value.Data.AsMemory(checked((int)offset), length),
            ChangeVersionOnRangeRead ? "changed" : value.Version));
    }

    public ValueTask<CloudObjectMetadata?> HeadAsync(string objectKey, CancellationToken cancellationToken)
    {
        _total++;
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
        _total++;
        _puts++;
        if (condition is PantsCloudObjectWriteCondition.IfAbsent && _objects.ContainsKey(objectKey))
        {
            return ValueTask.FromResult(false);
        }

        _objects[objectKey] = (data.ToArray(), Guid.NewGuid().ToString("N"));
        return ValueTask.FromResult(true);
    }

    public ValueTask<CloudObjectListPage> ListPageAsync(
        string prefix,
        string? continuationToken,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new CloudObjectListPage([], null));

    public ValueTask<CloudObjectDeleteOutcome> DeleteAsync(
        string objectKey,
        CloudObjectDeleteCondition condition,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(CloudObjectDeleteOutcome.NotFound);
}
