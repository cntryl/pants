using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class SstBlockCacheTests
{
    [Fact]
    public void ShouldCreateSixteenIndependentShardsByDefault()
    {
        var cache = new SstBlockCache(PantsBlockCachePolicy.Lru, 1024);

        Assert.Equal(16, cache.ShardCount);
    }

    [Fact]
    public async Task ShouldNotContendAcrossDifferentShards()
    {
        ControllableSstBlockCachePolicy[] policies = [new(), new()];
        var cache = new SstBlockCache(
            PantsBlockCachePolicy.Lru,
            32,
            policies.Length,
            shard => policies[shard]);
        var first = FindKeyForShard(cache, 0);
        var second = FindKeyForShard(cache, 1);
        Assert.True(cache.Add(first, [1]));
        Assert.True(cache.Add(second, [2]));
        policies[0].PauseAccess = true;

        var blockedRead = Task.Factory.StartNew(
            () => cache.TryGet(first, out _),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(policies[0].AccessEntered.Wait(TestTimeouts.Expected));
        try
        {
            var independentRead = Task.Factory.StartNew(
                () => cache.TryGet(second, out _),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Assert.True(await independentRead.WaitAsync(TestTimeouts.Expected));
        }
        finally
        {
            policies[0].ReleaseAccess.Set();
        }

        Assert.True(await blockedRead);
    }

    [Fact]
    public void ShouldUseRecencyForLruEviction()
    {
        var cache = new SstBlockCache(
            PantsBlockCachePolicy.Lru,
            2,
            1);
        SstBlockCacheKey hot = new(Sst("sst"), 0);
        SstBlockCacheKey recent = new(Sst("sst"), 1);
        SstBlockCacheKey incoming = new(Sst("sst"), 2);
        Assert.True(cache.Add(hot, [1]));
        Assert.True(cache.Add(recent, [2]));
        Assert.True(cache.TryGet(hot, out _));
        Assert.True(cache.TryGet(recent, out _));

        Assert.True(cache.Add(incoming, [3]));

        Assert.False(cache.TryGet(hot, out _));
        Assert.True(cache.TryGet(recent, out _));
        Assert.True(cache.TryGet(incoming, out _));
    }

    [Fact]
    public void ShouldRetainHotBlocksBetterThanLruGivenZipfianReadsAndOnePassScan()
    {
        var lruSurvivors = CountHotSurvivors(PantsBlockCachePolicy.Lru);
        var tinyLfuSurvivors = CountHotSurvivors(PantsBlockCachePolicy.TinyLfu);
        var clockProSurvivors = CountHotSurvivors(PantsBlockCachePolicy.ClockPro);

        Assert.Equal(0, lruSurvivors);
        Assert.True(tinyLfuSurvivors > lruSurvivors);
        Assert.True(clockProSurvivors > lruSurvivors);
    }

    [Fact]
    public void ShouldSelectTinyLfuVictimOutsideTheRecentSampleWindow()
    {
        var policy = new TinyLfuSstBlockCachePolicy();
        SstBlockCacheKey oldest = new(Sst("oldest"), 0);
        policy.RecordAccess(oldest);
        for (var index = 0; index < TinyLfuSstBlockCachePolicy.WindowSize; index++)
        {
            policy.RecordAccess(new SstBlockCacheKey(Sst("recent"), index));
        }

        Assert.True(policy.TrySelectVictim(out var victim));
        Assert.Equal(oldest, victim);
    }

    [Fact]
    public void ShouldKeepAdmittingTinyLfuBlocksAfterMoreThanOneSampleWindow()
    {
        var cache = new SstBlockCache(PantsBlockCachePolicy.TinyLfu, 128, 1);
        for (var index = 0; index < 128; index++)
        {
            Assert.True(cache.Add(new SstBlockCacheKey(Sst("resident"), index), [1]));
        }

        for (var hotIndex = 0; hotIndex < 10; hotIndex++)
        {
            Assert.True(cache.TryGet(new SstBlockCacheKey(Sst("resident"), hotIndex), out _));
        }

        for (var index = 0; index < 256; index++)
        {
            Assert.True(cache.Add(new SstBlockCacheKey(Sst("incoming"), index), [1]));
        }

        Assert.Equal(128, cache.Count);
        Assert.Equal(128, cache.UsedBytes);
    }

    [Fact]
    public void ShouldKeepClockProEvictionMovingWhenMostResidentsWereReused()
    {
        var cache = new SstBlockCache(PantsBlockCachePolicy.ClockPro, 16, 1);
        for (var index = 0; index < 16; index++)
        {
            var key = new SstBlockCacheKey(Sst("resident"), index);
            Assert.True(cache.Add(key, [1]));
            Assert.True(cache.TryGet(key, out _));
        }

        for (var index = 0; index < 64; index++)
        {
            Assert.True(cache.Add(new SstBlockCacheKey(Sst("incoming"), index), [1]));
        }

        Assert.Equal(16, cache.Count);
        Assert.Equal(16, cache.UsedBytes);
    }

    [Theory]
    [InlineData(PantsBlockCachePolicy.TinyLfu)]
    [InlineData(PantsBlockCachePolicy.ClockPro)]
    public void ShouldRetainHotBlocksAcrossFiveTinyLfuWindowsOfOneHitScan(
        PantsBlockCachePolicy policy)
    {
        var cache = new SstBlockCache(policy, 4, 1);
        SstBlockCacheKey[] hot = [new(Sst("hot"), 0), new(Sst("hot"), 1)];
        Assert.True(cache.Add(hot[0], [1]));
        Assert.True(cache.Add(hot[1], [1]));
        Assert.True(cache.Add(new SstBlockCacheKey(Sst("cold"), 0), [1]));
        Assert.True(cache.Add(new SstBlockCacheKey(Sst("cold"), 1), [1]));
        for (var access = 0; access < 50; access++)
        {
            Assert.True(cache.TryGet(hot[0], out _));
            Assert.True(cache.TryGet(hot[1], out _));
        }

        for (var scanBlock = 0;
             scanBlock < TinyLfuSstBlockCachePolicy.WindowSize * 5;
             scanBlock++)
        {
            Assert.True(cache.Add(new SstBlockCacheKey(Sst("scan"), scanBlock), [1]));
        }

        Assert.All(hot, key => Assert.True(cache.TryGet(key, out _)));
    }

    [Theory]
    [InlineData(PantsBlockCachePolicy.TinyLfu)]
    [InlineData(PantsBlockCachePolicy.ClockPro)]
    public async Task ShouldKeepExactCapacityAccountingUnderConcurrentFlood(
        PantsBlockCachePolicy policy)
    {
        const int workerCount = 8;
        const int operationsPerWorker = 100;
        const int entrySize = 16;
        const long capacity = 4_096;
        var cache = new SstBlockCache(policy, capacity, 1);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = Enumerable.Range(0, workerCount).Select(worker => Task.Run(async () =>
        {
            await start.Task;
            for (var operation = 0; operation < operationsPerWorker; operation++)
            {
                var fileName = $"worker-{worker}-batch-{operation / 10}";
                var key = new SstBlockCacheKey(Sst(fileName), operation);
                _ = cache.Add(key, new byte[entrySize]);
                if (operation % 3 == 0)
                {
                    _ = cache.TryGet(key, out _);
                }

                if (operation % 20 == 9)
                {
                    cache.RemoveFile(fileName);
                }

                Assert.InRange(cache.UsedBytes, 0, capacity);
            }
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(workers).WaitAsync(TestTimeouts.Expected);

        Assert.InRange(cache.UsedBytes, 0, capacity);
        Assert.Equal(cache.Count * entrySize, cache.UsedBytes);
    }

    [Fact]
    public void ShouldRemoveAllCachedBlocksGivenObsoleteSst()
    {
        var cache = new SstBlockCache(
            PantsBlockCachePolicy.Lru,
            3,
            1);
        Assert.True(cache.Add(new SstBlockCacheKey(Sst("obsolete.sst"), 0), [1]));
        Assert.True(cache.Add(new SstBlockCacheKey(Sst("obsolete.sst"), 1), [2]));
        Assert.True(cache.Add(new SstBlockCacheKey(Sst("live.sst"), 0), [3]));

        cache.RemoveFile("obsolete.sst");

        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet(new SstBlockCacheKey(Sst("obsolete.sst"), 0), out _));
        Assert.True(cache.TryGet(new SstBlockCacheKey(Sst("live.sst"), 0), out _));
    }

    [Fact]
    public void ShouldIsolateBlocksOfSameNameByManifestIdentityAndRemoveEveryIdentityByName()
    {
        var cache = new SstBlockCache(PantsBlockCachePolicy.Lru, 1024, 1);
        var original = new SstFileIdentity("reused.sst", 0, 7, 100, 1);
        var replacement = original with { SizeBytes = 200, ContentCrc32C = 2 };
        Assert.True(cache.Add(new SstBlockCacheKey(original, 0), [1]));

        Assert.False(cache.TryGet(new SstBlockCacheKey(replacement, 0), out _));
        Assert.True(cache.Add(new SstBlockCacheKey(replacement, 0), [2]));
        Assert.True(cache.TryGet(new SstBlockCacheKey(original, 0), out var originalBlock));
        Assert.True(cache.TryGet(new SstBlockCacheKey(replacement, 0), out var replacementBlock));
        Assert.Equal(new byte[] { 1 }, originalBlock!.Content.ToArray());
        Assert.Equal(new byte[] { 2 }, replacementBlock!.Content.ToArray());

        cache.RemoveFile("reused.sst");

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void ShouldOwnCachedBlockContent()
    {
        var cache = new SstBlockCache(
            PantsBlockCachePolicy.Lru,
            4,
            1);
        SstBlockCacheKey key = new(Sst("sst"), 0);
        byte[] source = [1, 2, 3, 4];
        Assert.True(cache.Add(key, source));

        source[0] = 99;

        Assert.True(cache.TryGet(key, out var cached));
        Assert.Equal([1, 2, 3, 4], Assert.IsType<SstBlockCacheEntry>(cached).Content.ToArray());
    }

    [Fact]
    public void ShouldHandleZeroCapacityAndRapidManagedLifecycle()
    {
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var cache = new SstBlockCache(
                PantsBlockCachePolicy.Lru,
                0,
                1);
            Assert.False(cache.Add(new SstBlockCacheKey(Sst("sst"), iteration), [1]));
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.UsedBytes);
        }
    }

    static int CountHotSurvivors(PantsBlockCachePolicy policy)
    {
        var cache = new SstBlockCache(policy, 4, 1);
        SstBlockCacheKey[] hot = [new(Sst("hot"), 0), new(Sst("hot"), 1)];
        Assert.True(cache.Add(hot[0], [0]));
        Assert.True(cache.Add(hot[1], [1]));
        Assert.True(cache.Add(new SstBlockCacheKey(Sst("cold"), 0), [2]));
        Assert.True(cache.Add(new SstBlockCacheKey(Sst("cold"), 1), [3]));
        for (var access = 0; access < 50; access++)
        {
            Assert.True(cache.TryGet(hot[0], out _));
            if (access % 3 == 0)
            {
                Assert.True(cache.TryGet(hot[1], out _));
            }
        }

        for (var scanBlock = 0; scanBlock < 8; scanBlock++)
        {
            _ = cache.Add(new SstBlockCacheKey(Sst("scan"), scanBlock), [checked((byte)scanBlock)]);
        }

        return hot.Count(key => cache.TryGet(key, out _));
    }

    static SstBlockCacheKey FindKeyForShard(SstBlockCache cache, int shard)
    {
        for (var index = 0; index < 10_000; index++)
        {
            var key = new SstBlockCacheKey(Sst($"sst-{index}"), index);
            if (cache.GetShardIndex(key) == shard)
            {
                return key;
            }
        }

        throw new InvalidOperationException($"Could not find a key for shard {shard}.");
    }

    static SstFileIdentity Sst(string name) => new(name, 0, 0, 0, null);
}
