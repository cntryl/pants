using Cntryl.Pants.Storage.Internal.Cache;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class AsyncSstReaderCacheTests
{
    [Fact]
    public async Task ShouldFetchMetadataOnceForRepeatedOpensOfOneSst()
    {
        using var directory = new TemporaryDirectory();
        var path = CreateSst(directory.Path);
        var counter = new ReadCounter();
        using var cache = new AsyncSstReaderCache(long.MaxValue);
        var file = new FileMeta { Name = "a.sst" };

        for (var index = 0; index < 5; index++)
        {
            await using var reader = await cache.GetOrOpenAsync(
                file,
                token => OpenAsync(path, file, counter, token),
                CancellationToken.None);
            Assert.True(reader.DataBlockCount > 0);
        }

        Assert.Equal(1, counter.Opens);
    }

    [Fact]
    public async Task ShouldShareOneOpenAcrossConcurrentFirstOpens()
    {
        using var directory = new TemporaryDirectory();
        var path = CreateSst(directory.Path);
        var counter = new ReadCounter();
        using var release = new ManualResetEventSlim();
        using var cache = new AsyncSstReaderCache(long.MaxValue);
        var file = new FileMeta { Name = "a.sst" };
        var opens = Enumerable.Range(0, 8)
            .Select(_ => cache.GetOrOpenAsync(
                file,
                async token =>
                {
                    await Task.Run(() => Assert.True(release.Wait(TestTimeouts.Expected)), token);
                    return await OpenAsync(path, file, counter, token);
                },
                CancellationToken.None).AsTask())
            .ToArray();

        release.Set();
        var readers = await Task.WhenAll(opens).WaitAsync(TestTimeouts.Expected);

        Assert.Equal(1, counter.Opens);
        foreach (var reader in readers)
        {
            await reader.DisposeAsync();
        }
    }

    [Fact]
    public async Task ShouldFailEveryWaiterAndAllowRetryWhenTheSharedOpenFails()
    {
        using var directory = new TemporaryDirectory();
        var path = CreateSst(directory.Path);
        var counter = new ReadCounter();
        using var cache = new AsyncSstReaderCache(long.MaxValue);
        var file = new FileMeta { Name = "a.sst" };

        await Assert.ThrowsAsync<IOException>(async () =>
            await cache.GetOrOpenAsync(
                file,
                _ => throw new IOException("injected"),
                CancellationToken.None));
        await using var reader = await cache.GetOrOpenAsync(
            file,
            token => OpenAsync(path, file, counter, token),
            CancellationToken.None);

        Assert.Equal(1, counter.Opens);
        Assert.True(reader.DataBlockCount > 0);
    }

    [Fact]
    public async Task ShouldKeepFewerReadersThanSstsWhenOverBudget()
    {
        using var directory = new TemporaryDirectory();
        var path = CreateSst(directory.Path);
        long readerBytes;
        await using (var probe = await AsyncSstReader.OpenAsync(
                         LocalAsyncSstSource.Open(path),
                         new FileMeta { Name = "probe.sst" },
                         CancellationToken.None))
        {
            readerBytes = probe.EstimatedMetadataBytes;
        }

        using var cache = new AsyncSstReaderCache(readerBytes * 3);
        var counter = new ReadCounter();
        for (var index = 0; index < 100; index++)
        {
            var file = new FileMeta { Name = $"{index}.sst" };
            await using var reader = await cache.GetOrOpenAsync(
                file,
                token => OpenAsync(path, file, counter, token),
                CancellationToken.None);
        }

        Assert.True(cache.CachedReaderCount <= 3);
    }

    [Fact]
    public async Task ShouldKeepEvictedReaderUsableUntilItsHolderReleasesIt()
    {
        using var directory = new TemporaryDirectory();
        var path = CreateSst(directory.Path);
        var counter = new ReadCounter();
        using var cache = new AsyncSstReaderCache(long.MaxValue);
        var file = new FileMeta { Name = "a.sst" };
        await using var held = await cache.GetOrOpenAsync(
            file,
            token => OpenAsync(path, file, counter, token),
            CancellationToken.None);

        cache.RemoveFile("a.sst");

        Assert.True(held.DataBlockCount > 0);
        Assert.Equal(0, cache.CachedReaderCount);
    }

    static async ValueTask<AsyncSstReader> OpenAsync(
        string path,
        FileMeta file,
        ReadCounter counter,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref counter.Opens);
        return await AsyncSstReader.OpenAsync(
            LocalAsyncSstSource.Open(path),
            file,
            cancellationToken);
    }

    static string CreateSst(string directory)
    {
        var path = Path.Combine(directory, "reader.sst");
        var entries = Enumerable.Range(0, 128)
            .Select(index => new SstEntry(
                TestBytes.FromString($"key-{index:0000}"),
                new byte[1024],
                checked((ulong)index + 1),
                null,
                false))
            .ToArray();
        File.WriteAllBytes(
            path,
            SstCodec.Encode(entries, [], PantsPerformanceGoal.Latency));
        return path;
    }

    sealed class ReadCounter
    {
        public int Opens;
    }
}
