using System.Collections.Concurrent;

namespace Cntryl.Pants.Tests.Storage;

public sealed class SstReaderCacheTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void ShouldCacheParsedReaderAndOwnItsFileHandle()
    {
        using var directory = new TemporaryDirectory();
        var (path, entries) = CreateSst(directory.Path);
        using var cache = new SstReaderCache();
        using var firstLease = cache.GetOrAdd("reader.sst", path, out var firstHit);
        using var secondLease = cache.GetOrAdd("reader.sst", path, out var secondHit);
        var first = firstLease.Reader;
        var second = secondLease.Reader;
        var decision = second.GetPointReadDecision(entries[64].Key);
        var block = second.ReadDataBlock(decision.CandidateBlockIndex);

        Assert.False(firstHit);
        Assert.True(secondHit);
        Assert.Same(first, second);
        Assert.True(SstCodec.DataBlockContainsKey(block, entries[64].Key));
        Assert.Equal(["reader.sst"], cache.SnapshotFiles());

        cache.RemoveFile("reader.sst");

        Assert.False(first.IsDisposed);
        Assert.Empty(cache.SnapshotFiles());
        secondLease.Dispose();
        Assert.False(first.IsDisposed);
        firstLease.Dispose();
        Assert.True(first.IsDisposed);
    }

    [Fact]
    public async Task ShouldKeepAnInFlightReaderValidUntilItsLeaseIsReleased()
    {
        using var directory = new TemporaryDirectory();
        var (path, entries) = CreateSst(directory.Path);
        using var cache = new SstReaderCache();
        using var lease = cache.GetOrAdd("reader.sst", path, out _);
        var reader = lease.Reader;
        var decision = reader.GetPointReadDecision(entries[64].Key);

        await Task.Run(() => cache.RemoveFile("reader.sst"));
        var block = reader.ReadDataBlock(decision.CandidateBlockIndex);

        Assert.True(SstCodec.DataBlockContainsKey(block, entries[64].Key));
        Assert.False(reader.IsDisposed);
        lease.Dispose();
        Assert.True(reader.IsDisposed);
    }

    [Fact]
    public async Task ShouldDisposeEveryLosingReaderFromConcurrentFirstCreation()
    {
        const int callerCount = 8;
        using var directory = new TemporaryDirectory();
        var (path, _) = CreateSst(directory.Path);
        var createdReaders = new ConcurrentBag<SstReader>();
        using var allOpening = new CountdownEvent(callerCount);
        using var releaseOpening = new ManualResetEventSlim();
        using var cache = new SstReaderCache(openPath =>
        {
            var reader = SstReader.Open(openPath);
            createdReaders.Add(reader);
            allOpening.Signal();
            Assert.True(releaseOpening.Wait(AssertionTimeout));
            return reader;
        });
        using var start = new ManualResetEventSlim();
        var acquisitions = Enumerable.Range(0, callerCount)
            .Select(callerIndex => Task.Factory.StartNew(
                () =>
                {
                    Assert.True(start.Wait(AssertionTimeout));
                    return cache.GetOrAdd("reader.sst", path, out _);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();

        start.Set();
        try
        {
            Assert.True(allOpening.Wait(AssertionTimeout));
        }
        finally
        {
            releaseOpening.Set();
        }

        var leases = await Task.WhenAll(acquisitions).WaitAsync(AssertionTimeout);
        try
        {
            var winner = leases[0].Reader;
            Assert.All(leases, lease => Assert.Same(winner, lease.Reader));
            Assert.Equal(callerCount, createdReaders.Count);
            Assert.All(createdReaders.Where(reader => !ReferenceEquals(reader, winner)),
                static reader => Assert.True(reader.IsDisposed));
            Assert.False(winner.IsDisposed);
            Assert.Equal(["reader.sst"], cache.SnapshotFiles());
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    [Fact]
    public async Task ShouldRejectAndDisposeAReaderWhoseOpenRacesWithCacheDisposal()
    {
        using var directory = new TemporaryDirectory();
        var (path, _) = CreateSst(directory.Path);
        using var opening = new ManualResetEventSlim();
        using var releaseOpening = new ManualResetEventSlim();
        SstReader? created = null;
        var cache = new SstReaderCache(openPath =>
        {
            created = SstReader.Open(openPath);
            opening.Set();
            Assert.True(releaseOpening.Wait(AssertionTimeout));
            return created;
        });
        var acquisition = Task.Run(() => cache.GetOrAdd("reader.sst", path, out _));
        Assert.True(opening.Wait(AssertionTimeout));

        var firstDisposal = Task.Run(cache.Dispose);
        Assert.True(SpinWait.SpinUntil(() => cache.IsDisposed, AssertionTimeout));
        var secondDisposal = Task.Run(cache.Dispose);
        Assert.False(firstDisposal.IsCompleted);
        Assert.False(secondDisposal.IsCompleted);
        releaseOpening.Set();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await acquisition);
        await Task.WhenAll(firstDisposal, secondDisposal).WaitAsync(AssertionTimeout);
        Assert.NotNull(created);
        Assert.True(created.IsDisposed);
        Assert.Empty(cache.SnapshotFiles());
    }

    [Fact]
    public async Task ShouldRejectAndDisposeAReaderWhoseOpenRacesWithFileRemoval()
    {
        using var directory = new TemporaryDirectory();
        var (path, _) = CreateSst(directory.Path);
        using var opening = new ManualResetEventSlim();
        using var releaseOpening = new ManualResetEventSlim();
        SstReader? created = null;
        using var cache = new SstReaderCache(openPath =>
        {
            created = SstReader.Open(openPath);
            opening.Set();
            Assert.True(releaseOpening.Wait(AssertionTimeout));
            return created;
        });
        var acquisition = Task.Run(() => cache.GetOrAdd("reader.sst", path, out _));
        Assert.True(opening.Wait(AssertionTimeout));

        cache.RemoveFile("reader.sst");
        releaseOpening.Set();

        await Assert.ThrowsAsync<FileNotFoundException>(async () => await acquisition);
        Assert.NotNull(created);
        Assert.True(created.IsDisposed);
        Assert.Empty(cache.SnapshotFiles());
    }

    static (string Path, SstEntry[] Entries) CreateSst(string directory)
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
        return (path, entries);
    }
}
