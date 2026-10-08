using System.Collections.Concurrent;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class SstReaderCacheTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void ShouldCacheParsedReaderAndOwnItsFileHandle()
    {
        using var directory = new TemporaryDirectory();
        var (path, entries) = CreateSst(directory.Path);
        using var cache = new SstReaderCache();
        using var firstLease = cache.GetOrAdd(Sst("reader.sst"), path, out var firstHit);
        using var secondLease = cache.GetOrAdd(Sst("reader.sst"), path, out var secondHit);
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
        using var lease = cache.GetOrAdd(Sst("reader.sst"), path, out _);
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
    public async Task ShouldOpenOnceWhenCallersFirstOpenTheSameSstConcurrently()
    {
        const int callerCount = 8;
        using var directory = new TemporaryDirectory();
        var (path, _) = CreateSst(directory.Path);
        var opens = 0;
        using var openStarted = new ManualResetEventSlim();
        using var releaseOpening = new ManualResetEventSlim();
        using var cache = new SstReaderCache(openPath =>
        {
            Interlocked.Increment(ref opens);
            openStarted.Set();
            Assert.True(releaseOpening.Wait(AssertionTimeout));
            return SstReader.Open(openPath);
        });
        var acquisitions = Enumerable.Range(0, callerCount)
            .Select(callerIndex => Task.Run(() => cache.GetOrAdd(Sst("reader.sst"), path, out _)))
            .ToArray();

        Assert.True(openStarted.Wait(AssertionTimeout));
        releaseOpening.Set();
        var leases = await Task.WhenAll(acquisitions).WaitAsync(AssertionTimeout);
        try
        {
            Assert.Equal(1, Volatile.Read(ref opens));
            Assert.All(leases, lease => Assert.Same(leases[0].Reader, lease.Reader));
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
    public async Task ShouldOpenUnrelatedSstWhileAnotherOpenIsBlocked()
    {
        using var directory = new TemporaryDirectory();
        var (path, _) = CreateSst(directory.Path);
        using var blockedStarted = new ManualResetEventSlim();
        using var releaseBlocked = new ManualResetEventSlim();
        using var cache = new SstReaderCache(openPath =>
        {
            if (openPath.EndsWith("slow.sst", StringComparison.Ordinal))
            {
                blockedStarted.Set();
                Assert.True(releaseBlocked.Wait(AssertionTimeout));
            }

            return SstReader.Open(path);
        });
        var blocked = Task.Run(() => cache.GetOrAdd(Sst("slow.sst"), "slow.sst", out _));
        Assert.True(blockedStarted.Wait(AssertionTimeout));

        using var unrelated = await Task.Run(() => cache.GetOrAdd(Sst("cold.sst"), "cold.sst", out _))
            .WaitAsync(AssertionTimeout);

        releaseBlocked.Set();
        using var slow = await blocked.WaitAsync(AssertionTimeout);
        Assert.NotSame(slow.Reader, unrelated.Reader);
    }

    [Fact]
    public void ShouldAllowRetryWhenTheOwningOpenFails()
    {
        using var directory = new TemporaryDirectory();
        var (path, _) = CreateSst(directory.Path);
        var attempts = 0;
        using var cache = new SstReaderCache(openPath =>
            Interlocked.Increment(ref attempts) == 1
                ? throw new IOException("injected")
                : SstReader.Open(openPath));

        Assert.Throws<IOException>(() => cache.GetOrAdd(Sst("reader.sst"), path, out _));
        using var lease = cache.GetOrAdd(Sst("reader.sst"), path, out var hit);

        Assert.False(hit);
        Assert.False(lease.Reader.IsDisposed);
    }

    [Fact]
    public void ShouldEvictLeastRecentlyUsedIdleReadersWhenOverBudget()
    {
        using var directory = new TemporaryDirectory();
        var (path, _) = CreateSst(directory.Path);
        long readerBytes;
        using (var probe = SstReader.Open(path))
        {
            readerBytes = probe.EstimatedMetadataBytes;
        }

        using var cache = new SstReaderCache(readerBytes * 3, SstReader.Open);
        for (var index = 0; index < 100; index++)
        {
            using var lease = cache.GetOrAdd(Sst($"reader-{index}.sst"), path, out _);
        }

        Assert.True(cache.SnapshotFiles().Count <= 3);
        Assert.Contains("reader-99.sst", cache.SnapshotFiles());
        Assert.DoesNotContain("reader-0.sst", cache.SnapshotFiles());
    }

    [Fact]
    public void ShouldNotReuseReaderWhenSameNameHasDifferentManifestIdentity()
    {
        using var directory = new TemporaryDirectory();
        var (path, _) = CreateSst(directory.Path);
        using var cache = new SstReaderCache();
        var original = new SstFileIdentity("reused.sst", 0, 7, 100, 1);
        var replacement = original with { SizeBytes = 200, ContentCrc32C = 2 };
        using var originalLease = cache.GetOrAdd(original, path, out _);

        using var replacementLease = cache.GetOrAdd(replacement, path, out var replacementHit);

        Assert.False(replacementHit);
        Assert.NotSame(originalLease.Reader, replacementLease.Reader);

        cache.RemoveFile("reused.sst");

        Assert.Empty(cache.SnapshotFiles());
        Assert.False(originalLease.Reader.IsDisposed);
        Assert.False(replacementLease.Reader.IsDisposed);
    }

    [Fact]
    public void ShouldNeverEvictAReaderThatIsStillLeased()
    {
        using var directory = new TemporaryDirectory();
        var (path, _) = CreateSst(directory.Path);
        using var cache = new SstReaderCache(1, SstReader.Open);
        using var held = cache.GetOrAdd(Sst("held.sst"), path, out _);
        using (cache.GetOrAdd(Sst("other.sst"), path, out _))
        {
        }

        Assert.False(held.Reader.IsDisposed);
        Assert.Contains("held.sst", cache.SnapshotFiles());
        Assert.DoesNotContain("other.sst", cache.SnapshotFiles());
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
        var acquisition = Task.Run(() => cache.GetOrAdd(Sst("reader.sst"), path, out _));
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
        var acquisition = Task.Run(() => cache.GetOrAdd(Sst("reader.sst"), path, out _));
        Assert.True(opening.Wait(AssertionTimeout));

        cache.RemoveFile("reader.sst");
        releaseOpening.Set();

        await Assert.ThrowsAsync<FileNotFoundException>(async () => await acquisition);
        Assert.NotNull(created);
        Assert.True(created.IsDisposed);
        Assert.Empty(cache.SnapshotFiles());
    }

    static SstFileIdentity Sst(string name) => new(name, 0, 0, 0, null);

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
