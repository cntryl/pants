namespace Cntryl.Pants.Support.TestDoubles;

sealed class ThrowingCloudObjectStore : ICloudObjectStore
{
    public ValueTask<CloudObject?> GetAsync(
        string objectKey,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not used by this test double.");

    public ValueTask<CloudObjectMetadata?> HeadAsync(
        string objectKey,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not used by this test double.");

    public ValueTask<bool> PutAsync(
        string objectKey,
        ReadOnlyMemory<byte> data,
        CloudObjectWriteCondition condition,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not used by this test double.");

    public ValueTask<CloudObjectListPage> ListPageAsync(
        string prefix,
        string? continuationToken,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not used by this test double.");

    public ValueTask<CloudObjectDeleteOutcome> DeleteAsync(
        string objectKey,
        CloudObjectDeleteCondition condition,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Not used by this test double.");

    public ValueTask DisposeAsync() =>
        throw new InvalidOperationException("Simulated disposal failure.");
}
