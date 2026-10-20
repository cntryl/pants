using Cntryl.Pants.Storage.Internal.Cache;
using Cntryl.Pants.Storage.Internal.Sst;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class AsyncSstReaderCacheTests
{
    [Fact]
    public async Task ShouldEvictCachedAsyncReaderBeforeOpeningUncachedReaderWithinHandleBudget()
    {
        using var directory = new TemporaryDirectory();
        var path = CreateSst(directory.Path);
        var counter = new ReadCounter();
        var handleBudget = new SstReaderHandleBudget(1);
        using var cache = new AsyncSstReaderCache(long.MaxValue, handleBudget);
        var cachedFile = new FileMeta { Name = "cached.sst" };

        await using (var cached = await cache.GetOrOpenAsync(
                         cachedFile,
                         token => OpenAsync(path, cachedFile, counter, token),
                         CancellationToken.None))
        {
        }

        var uncachedFile = new FileMeta { Name = "uncached.sst" };
        await using (var uncached = await cache.OpenUncachedAsync(
                         token => OpenAsync(path, uncachedFile, counter, token),
                         CancellationToken.None))
        {
            Assert.Equal(2, counter.Opens);
            Assert.Equal(1, handleBudget.Current);
            Assert.Equal(0, cache.CachedReaderCount);
            Assert.True(uncached.DataBlockCount > 0);

            var blockedFile = new FileMeta { Name = "blocked.sst" };
            await Assert.ThrowsAsync<PantsResourceLimitException>(async () =>
                await cache.GetOrOpenAsync(
                    blockedFile,
                    token => OpenAsync(path, blockedFile, counter, token),
                    CancellationToken.None));
            Assert.Equal(2, counter.Opens);
        }

        Assert.Equal(0, handleBudget.Current);
    }

    [Fact]
    public async Task ShouldReleaseUncachedAsyncReaderReservationWhenOpenFails()
    {
        using var directory = new TemporaryDirectory();
        var path = CreateSst(directory.Path);
        var counter = new ReadCounter();
        var handleBudget = new SstReaderHandleBudget(1);
        using var cache = new AsyncSstReaderCache(long.MaxValue, handleBudget);

        await Assert.ThrowsAsync<IOException>(async () =>
            await cache.OpenUncachedAsync(
                _ => throw new IOException("injected"),
                CancellationToken.None));

        Assert.Equal(0, handleBudget.Current);
        var file = new FileMeta { Name = "retry.sst" };
        await using var reader = await cache.OpenUncachedAsync(
            token => OpenAsync(path, file, counter, token),
            CancellationToken.None);

        Assert.Equal(1, handleBudget.Current);
        Assert.Equal(1, counter.Opens);
        Assert.True(reader.DataBlockCount > 0);
    }

    [Fact]
    public async Task ShouldReleaseUncachedAsyncReaderReservationWhenParsingFailsAfterSourceOpen()
    {
        using var directory = new TemporaryDirectory();
        var path = CreateSst(directory.Path);
        var handleBudget = new SstReaderHandleBudget(1);
        using var cache = new AsyncSstReaderCache(long.MaxValue, handleBudget);
        var file = new FileMeta { Name = "wrong-length.sst", SizeBytes = 1 };

        await Assert.ThrowsAsync<PantsCorruptionException>(async () =>
            await cache.OpenUncachedAsync(
                token => AsyncSstReader.OpenAsync(
                    LocalAsyncSstSource.Open(path),
                    file,
                    token),
                CancellationToken.None));

        Assert.Equal(0, handleBudget.Current);
    }

    [Fact]
    public async Task ShouldReleaseUncachedAsyncReaderReservationWhenCacheIsDisposedDuringOpen()
    {
        using var directory = new TemporaryDirectory();
        var path = CreateSst(directory.Path);
        var handleBudget = new SstReaderHandleBudget(1);
        using var cache = new AsyncSstReaderCache(long.MaxValue, handleBudget);
        var file = new FileMeta { Name = "opening.sst" };
        var openStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOpen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = cache.OpenUncachedAsync(
            async token =>
            {
                openStarted.SetResult();
                await releaseOpen.Task.WaitAsync(token);
                return await AsyncSstReader.OpenAsync(
                    LocalAsyncSstSource.Open(path),
                    file,
                    token);
            },
            CancellationToken.None).AsTask();

        await openStarted.Task.WaitAsync(TestTimeouts.Expected);
        Assert.Equal(1, handleBudget.Current);
        cache.Dispose();
        releaseOpen.SetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await opening.WaitAsync(TestTimeouts.Expected));

        Assert.Equal(0, handleBudget.Current);
    }

    [Fact]
    public async Task ShouldRejectOpenWhenAllAsyncReaderHandlesRemainLeased()
    {
        using var directory = new TemporaryDirectory();
        var path = CreateSst(directory.Path);
        var counter = new ReadCounter();
        var handleBudget = new SstReaderHandleBudget(1);
        using var cache = new AsyncSstReaderCache(long.MaxValue, handleBudget);
        var heldFile = new FileMeta { Name = "held.sst" };

        await using (var held = await cache.GetOrOpenAsync(
                         heldFile,
                         token => OpenAsync(path, heldFile, counter, token),
                         CancellationToken.None))
        {
            var rejectedFile = new FileMeta { Name = "rejected.sst" };
            var error = await Assert.ThrowsAsync<PantsResourceLimitException>(async () =>
                await cache.GetOrOpenAsync(
                    rejectedFile,
                    token => OpenAsync(path, rejectedFile, counter, token),
                    CancellationToken.None));

            Assert.Contains("1-handle budget", error.Message, StringComparison.Ordinal);
            Assert.Equal(1, counter.Opens);
            Assert.Equal(1, handleBudget.Current);
            Assert.True(held.DataBlockCount > 0);
        }

        Assert.Equal(0, handleBudget.Current);
    }

    [Fact]
    public async Task ShouldEvictCachedAsyncReaderBeforeOpeningWithinHandleBudget()
    {
        using var directory = new TemporaryDirectory();
        var path = CreateSst(directory.Path);
        var counter = new ReadCounter();
        var handleBudget = new SstReaderHandleBudget(1);
        using var cache = new AsyncSstReaderCache(long.MaxValue, handleBudget);
        var firstFile = new FileMeta { Name = "first.sst" };

        await using (var first = await cache.GetOrOpenAsync(
                         firstFile,
                         token => OpenAsync(path, firstFile, counter, token),
                         CancellationToken.None))
        {
        }

        var nextFile = new FileMeta { Name = "next.sst" };
        await using var next = await cache.GetOrOpenAsync(
            nextFile,
            token => OpenAsync(path, nextFile, counter, token),
            CancellationToken.None);

        Assert.Equal(2, counter.Opens);
        Assert.Equal(1, handleBudget.Current);
        Assert.Equal(1, cache.CachedReaderCount);
        Assert.True(next.DataBlockCount > 0);
    }

    [Fact]
    public async Task ShouldReserveAsyncReaderHandleBeforeStartingOpen()
    {
        using var directory = new TemporaryDirectory();
        var path = CreateSst(directory.Path);
        var counter = new ReadCounter();
        var handleBudget = new SstReaderHandleBudget(1);
        using var cache = new AsyncSstReaderCache(long.MaxValue, handleBudget);
        var firstFile = new FileMeta { Name = "opening.sst" };
        var openStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOpen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = cache.GetOrOpenAsync(
            firstFile,
            async token =>
            {
                Interlocked.Increment(ref counter.Opens);
                openStarted.SetResult();
                await releaseOpen.Task.WaitAsync(token);
                return await AsyncSstReader.OpenAsync(
                    LocalAsyncSstSource.Open(path),
                    firstFile,
                    token);
            },
            CancellationToken.None).AsTask();

        await openStarted.Task.WaitAsync(TestTimeouts.Expected);
        Assert.Equal(1, handleBudget.Current);

        await Assert.ThrowsAsync<PantsResourceLimitException>(async () =>
            await cache.GetOrOpenAsync(
                new FileMeta { Name = "blocked.sst" },
                token => OpenAsync(path, new FileMeta { Name = "blocked.sst" }, counter, token),
                CancellationToken.None));

        Assert.Equal(1, counter.Opens);
        releaseOpen.SetResult();
        await using var opened = await opening.WaitAsync(TestTimeouts.Expected);
        Assert.True(opened.DataBlockCount > 0);
    }

    [Fact]
    public async Task ShouldReleaseAsyncReaderReservationWhenOpenFails()
    {
        using var directory = new TemporaryDirectory();
        var path = CreateSst(directory.Path);
        var counter = new ReadCounter();
        var handleBudget = new SstReaderHandleBudget(1);
        using var cache = new AsyncSstReaderCache(long.MaxValue, handleBudget);
        var file = new FileMeta { Name = "a.sst" };

        await Assert.ThrowsAsync<IOException>(async () =>
            await cache.GetOrOpenAsync(
                file,
                _ => throw new IOException("injected"),
                CancellationToken.None));

        Assert.Equal(0, handleBudget.Current);
        await using var reader = await cache.GetOrOpenAsync(
            file,
            token => OpenAsync(path, file, counter, token),
            CancellationToken.None);

        Assert.Equal(1, handleBudget.Current);
        Assert.Equal(1, counter.Opens);
        Assert.True(reader.DataBlockCount > 0);
    }

    [Fact]
    public async Task ShouldReleaseAsyncReaderReservationWhenParsingFailsAfterSourceOpen()
    {
        using var directory = new TemporaryDirectory();
        var path = CreateSst(directory.Path);
        var handleBudget = new SstReaderHandleBudget(1);
        using var cache = new AsyncSstReaderCache(long.MaxValue, handleBudget);
        var file = new FileMeta { Name = "wrong-length.sst", SizeBytes = 1 };

        await Assert.ThrowsAsync<PantsCorruptionException>(async () =>
            await cache.GetOrOpenAsync(
                file,
                token => AsyncSstReader.OpenAsync(
                    LocalAsyncSstSource.Open(path),
                    file,
                    token),
                CancellationToken.None));

        Assert.Equal(0, handleBudget.Current);
        Assert.Equal(0, cache.CachedReaderCount);
    }

    [Fact]
    public async Task ShouldReleaseAsyncReaderReservationWhenCacheIsDisposedDuringOpen()
    {
        using var directory = new TemporaryDirectory();
        var path = CreateSst(directory.Path);
        var handleBudget = new SstReaderHandleBudget(1);
        using var cache = new AsyncSstReaderCache(long.MaxValue, handleBudget);
        var file = new FileMeta { Name = "opening.sst" };
        var openStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOpen = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = cache.GetOrOpenAsync(
            file,
            async token =>
            {
                openStarted.SetResult();
                await releaseOpen.Task.WaitAsync(token);
                return await AsyncSstReader.OpenAsync(
                    LocalAsyncSstSource.Open(path),
                    file,
                    token);
            },
            CancellationToken.None).AsTask();

        await openStarted.Task.WaitAsync(TestTimeouts.Expected);
        Assert.Equal(1, handleBudget.Current);
        cache.Dispose();

        releaseOpen.SetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await opening.WaitAsync(TestTimeouts.Expected));

        Assert.Equal(0, handleBudget.Current);
    }

    [Fact]
    public async Task ShouldKeepAsyncReaderReservationWhenSourceDisposalFails()
    {
        using var directory = new TemporaryDirectory();
        var path = CreateSst(directory.Path);
        var handleBudget = new SstReaderHandleBudget(1);
        using var cache = new AsyncSstReaderCache(long.MaxValue, handleBudget);
        var file = new FileMeta { Name = "a.sst" };
        var source = new FailingDisposeAsyncSstSource(LocalAsyncSstSource.Open(path));
        var reader = await cache.GetOrOpenAsync(
            file,
            token => AsyncSstReader.OpenAsync(source, file, token),
            CancellationToken.None);

        cache.RemoveFile(file.Name);
        await Assert.ThrowsAsync<IOException>(async () => await reader.DisposeAsync());

        Assert.Equal(1, handleBudget.Current);
        await Assert.ThrowsAsync<PantsResourceLimitException>(async () =>
            await cache.GetOrOpenAsync(
                new FileMeta { Name = "blocked.sst" },
                _ => throw new Xunit.Sdk.XunitException("The source factory must not run."),
                CancellationToken.None));

        await source.CloseInnerAsync();
    }

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

    sealed class FailingDisposeAsyncSstSource(IAsyncSstSource inner) : IAsyncSstSource
    {
        public long Length => inner.Length;

        public ValueTask<byte[]> ReadExactlyAsync(
            long offset,
            int length,
            CancellationToken cancellationToken) =>
            inner.ReadExactlyAsync(offset, length, cancellationToken);

        public ValueTask DisposeAsync() =>
            ValueTask.FromException(new IOException("injected source close failure"));

        public ValueTask CloseInnerAsync() => inner.DisposeAsync();
    }
}
