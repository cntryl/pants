using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderSstPublisherTests
{
    [Fact]
    public async Task ShouldPublishNewSstOnceAndNeverTouchItAgain()
    {
        using var directory = new TemporaryDirectory();
        var store = new RangeStore();
        var publisher = new ProviderSstPublisher(store, static () => { });
        var path = WriteLocal(directory.Path, "a.sst", 200_000);

        await publisher.EnsurePublishedAsync("a.sst", path, [], CancellationToken.None);
        var callsAfterFirst = store.TotalCalls;
        await publisher.EnsurePublishedAsync("a.sst", path, [], CancellationToken.None);
        await publisher.EnsurePublishedAsync("a.sst", null, [], CancellationToken.None);

        Assert.Equal(1, store.Puts);
        Assert.Equal(callsAfterFirst, store.TotalCalls);
        Assert.Equal(0, store.WholeGets);
        Assert.True(store.RangeGets >= 4, "Verification should read the object in ranged pieces.");
        Assert.True(store.LargestRangeBytes <= ProviderSstPublisher.VerificationRangeBytes);
    }

    [Fact]
    public async Task ShouldIdentifyAlreadyPublishedSstByHeadWithoutDownloadingIt()
    {
        using var directory = new TemporaryDirectory();
        var store = new RangeStore();
        var path = WriteLocal(directory.Path, "a.sst", 100_000);
        await new ProviderSstPublisher(store, static () => { })
            .EnsurePublishedAsync("a.sst", path, [], CancellationToken.None);
        store.ResetCounters();
        var restarted = new ProviderSstPublisher(store, static () => { });

        await restarted.EnsurePublishedAsync("a.sst", path, [], CancellationToken.None);
        await restarted.EnsurePublishedAsync("a.sst", null, [], CancellationToken.None);

        Assert.Equal(0, store.Puts);
        Assert.Equal(0, store.WholeGets);
        Assert.Equal(0, store.RangeGets);
    }

    [Fact]
    public async Task ShouldFailWhenRemoteOnlySstIsMissing()
    {
        var publisher = new ProviderSstPublisher(new RangeStore(), static () => { });

        await Assert.ThrowsAsync<PantsRecoveryFailedException>(async () =>
            await publisher.EnsurePublishedAsync("gone.sst", null, [], CancellationToken.None));
    }

    [Fact]
    public async Task ShouldRejectVerificationWhenObjectIdentityChangesMidReadback()
    {
        using var directory = new TemporaryDirectory();
        var store = new RangeStore { ChangeVersionOnRangeRead = true };
        var path = WriteLocal(directory.Path, "a.sst", 200_000);
        var publisher = new ProviderSstPublisher(store, static () => { });

        await Assert.ThrowsAsync<PantsFencedException>(async () =>
            await publisher.EnsurePublishedAsync("a.sst", path, [], CancellationToken.None));
        Assert.False(publisher.IsProven("a.sst"));
    }

    static string WriteLocal(string directory, string name, int bytes)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, System.Security.Cryptography.RandomNumberGenerator.GetBytes(bytes));
        return path;
    }

    sealed class RangeStore : ICloudObjectStore
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
}
