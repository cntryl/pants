using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class AsyncSstScanSourceTests
{
    [Fact]
    public async Task ShouldHoldNoReaderUntilTheMergeAdvancesTheSource()
    {
        using var directory = new TemporaryDirectory();
        var path = CreateSst(directory.Path);
        var file = new FileMeta { Name = "a.sst" };
        var outstanding = 0;
        var opens = 0;

        async ValueTask<AsyncSstReader> OpenAsync(CancellationToken token)
        {
            Interlocked.Increment(ref opens);
            Interlocked.Increment(ref outstanding);
            var source = new TrackingSource(
                LocalAsyncSstSource.Open(path),
                () => Interlocked.Decrement(ref outstanding));
            return await AsyncSstReader.OpenAsync(source, file, token);
        }

        await using var scanSource = await AsyncSstScanSource.CreateAsync(
            file,
            await OpenAsync(CancellationToken.None),
            OpenAsync,
            PantsScanDirection.Forward,
            null,
            null,
            null);

        Assert.Equal(1, opens);
        Assert.Equal(0, Volatile.Read(ref outstanding));
        Assert.Equal("key-0000", System.Text.Encoding.UTF8.GetString(scanSource.SmallestKey));

        Assert.True(await scanSource.MoveNextAsync(CancellationToken.None));
        Assert.Equal(2, opens);
        Assert.Equal(1, Volatile.Read(ref outstanding));
        Assert.Equal("key-0000", System.Text.Encoding.UTF8.GetString(scanSource.Current.Key));

        await scanSource.DisposeAsync();
        Assert.Equal(0, Volatile.Read(ref outstanding));
        Assert.True(scanSource.DataBlocksRead > 0);
    }

    static string CreateSst(string directory)
    {
        var path = Path.Combine(directory, "scan.sst");
        var entries = Enumerable.Range(0, 128)
            .Select(index => new SstEntry(
                TestBytes.FromString($"key-{index:0000}"),
                new byte[256],
                checked((ulong)index + 1),
                null,
                false))
            .ToArray();
        File.WriteAllBytes(
            path,
            SstCodec.Encode(entries, [], PantsPerformanceGoal.Latency));
        return path;
    }

    sealed class TrackingSource(IAsyncSstSource inner, Action disposed) : IAsyncSstSource
    {
        public long Length => inner.Length;

        public ValueTask<byte[]> ReadExactlyAsync(
            long offset,
            int length,
            CancellationToken cancellationToken) =>
            inner.ReadExactlyAsync(offset, length, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            disposed();
        }
    }
}
