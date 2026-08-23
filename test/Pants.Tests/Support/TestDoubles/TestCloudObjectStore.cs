namespace Cntryl.Pants.Tests;

internal sealed class TestCloudObjectStore : ICloudObjectStore
{
    string? _objectKey;
    string? _version;
    int _putCount;

    public ReadOnlyMemory<byte> Data { get; set; }

    public CloudObjectWriteCondition? LastCondition { get; set; }

    public int PutCount => Volatile.Read(ref _putCount);

    public Func<CancellationToken, ValueTask>? BeforeNextPutAsync { get; set; }

    public ValueTask<CloudObject?> GetAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CloudObject? value = _version is null ||
                             !StringComparer.Ordinal.Equals(_objectKey, objectKey)
            ? null
            : new CloudObject(Data, _version);
        return ValueTask.FromResult(value);
    }

    public ValueTask<CloudObjectMetadata?> HeadAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CloudObjectMetadata? metadata = _version is null ||
                                        !StringComparer.Ordinal.Equals(_objectKey, objectKey)
            ? null
            : new CloudObjectMetadata(
                checked((ulong)Data.Length),
                _version,
                Generation: null,
                LastModifiedUtc: null);
        return ValueTask.FromResult(metadata);
    }

    public async ValueTask<bool> PutAsync(
        string objectKey,
        ReadOnlyMemory<byte> data,
        CloudObjectWriteCondition condition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _putCount);
        var beforePut = BeforeNextPutAsync;
        BeforeNextPutAsync = null;
        if (beforePut is not null)
        {
            await beforePut(cancellationToken).ConfigureAwait(false);
        }

        LastCondition = condition;
        var accepted = condition switch
        {
            CloudObjectWriteCondition.Unconditional => true,
            CloudObjectWriteCondition.IfAbsent => _version is null,
            CloudObjectWriteCondition.IfVersion expected =>
                StringComparer.Ordinal.Equals(expected.Version, _version),
            _ => false
        };
        if (accepted)
        {
            Data = data.ToArray();
            _objectKey = objectKey;
            _version = Guid.NewGuid().ToString("N");
        }

        return accepted;
    }

    public ValueTask<CloudObjectListPage> ListPageAsync(
        string prefix,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var objectKeys = new List<string>();
        if (continuationToken is null &&
            _objectKey is { } objectKey &&
            objectKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            objectKeys.Add(objectKey);
        }

        return ValueTask.FromResult(new CloudObjectListPage(objectKeys, continuationToken: null));
    }

    public ValueTask<CloudObjectDeleteOutcome> DeleteAsync(
        string objectKey,
        CloudObjectDeleteCondition condition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_version is null || !StringComparer.Ordinal.Equals(_objectKey, objectKey))
        {
            return ValueTask.FromResult(CloudObjectDeleteOutcome.NotFound);
        }

        if (condition is CloudObjectDeleteCondition.IfVersion expected &&
            !StringComparer.Ordinal.Equals(expected.Version, _version))
        {
            return ValueTask.FromResult(CloudObjectDeleteOutcome.ConditionNotMet);
        }

        Data = default;
        _objectKey = null;
        _version = null;
        return ValueTask.FromResult(CloudObjectDeleteOutcome.Deleted);
    }
}
