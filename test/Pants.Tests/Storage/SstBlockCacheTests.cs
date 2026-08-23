namespace Pants.Tests;

public sealed class SstBlockCacheTests
{
    [Fact]
    public void ShouldCreateSixteenIndependentShardsByDefault()
    {
        var cache = new SstBlockCache(PantsBlockCachePolicy.Lru, capacityBytes: 1024);

        Assert.Equal(16, cache.ShardCount);
    }

    [Fact]
    public async Task ShouldNotContendAcrossDifferentShards()
    {
        ControllableSstBlockCachePolicy[] policies = [new(), new()];
        var cache = new SstBlockCache(
            PantsBlockCachePolicy.Lru,
            capacityBytes: 32,
            shardCount: policies.Length,
            policyFactory: shard => policies[shard]);
        SstBlockCacheKey first = FindKeyForShard(cache, 0);
        SstBlockCacheKey second = FindKeyForShard(cache, 1);
        Assert.True(cache.Add(first, [1]));
        Assert.True(cache.Add(second, [2]));
        policies[0].PauseAccess = true;

        var blockedRead = Task.Factory.StartNew(
            () => cache.TryGet(first, out _),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        Assert.True(policies[0].AccessEntered.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            var independentRead = Task.Factory.StartNew(
                () => cache.TryGet(second, out _),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            Assert.True(await independentRead.WaitAsync(TimeSpan.FromSeconds(2)));
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
            capacityBytes: 2,
            shardCount: 1);
        SstBlockCacheKey hot = new("sst", 0);
        SstBlockCacheKey recent = new("sst", 1);
        SstBlockCacheKey incoming = new("sst", 2);
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
        int lruSurvivors = CountHotSurvivors(PantsBlockCachePolicy.Lru);
        int tinyLfuSurvivors = CountHotSurvivors(PantsBlockCachePolicy.TinyLfu);
        int clockProSurvivors = CountHotSurvivors(PantsBlockCachePolicy.ClockPro);

        Assert.Equal(0, lruSurvivors);
        Assert.True(tinyLfuSurvivors > lruSurvivors);
        Assert.True(clockProSurvivors > lruSurvivors);
    }

    [Fact]
    public void ShouldRemoveAllCachedBlocksGivenObsoleteSst()
    {
        var cache = new SstBlockCache(
            PantsBlockCachePolicy.Lru,
            capacityBytes: 3,
            shardCount: 1);
        Assert.True(cache.Add(new SstBlockCacheKey("obsolete.sst", 0), [1]));
        Assert.True(cache.Add(new SstBlockCacheKey("obsolete.sst", 1), [2]));
        Assert.True(cache.Add(new SstBlockCacheKey("live.sst", 0), [3]));

        cache.RemoveFile("obsolete.sst");

        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet(new SstBlockCacheKey("obsolete.sst", 0), out _));
        Assert.True(cache.TryGet(new SstBlockCacheKey("live.sst", 0), out _));
    }

    [Fact]
    public void ShouldOwnCachedBlockContent()
    {
        var cache = new SstBlockCache(
            PantsBlockCachePolicy.Lru,
            capacityBytes: 4,
            shardCount: 1);
        SstBlockCacheKey key = new("sst", 0);
        byte[] source = [1, 2, 3, 4];
        Assert.True(cache.Add(key, source));

        source[0] = 99;

        Assert.True(cache.TryGet(key, out SstBlockCacheEntry? cached));
        Assert.Equal([1, 2, 3, 4], Assert.IsType<SstBlockCacheEntry>(cached).Content.ToArray());
    }

    [Fact]
    public void ShouldHandleZeroCapacityAndRapidManagedLifecycle()
    {
        for (var iteration = 0; iteration < 50; iteration++)
        {
            var cache = new SstBlockCache(
                PantsBlockCachePolicy.Lru,
                capacityBytes: 0,
                shardCount: 1);
            Assert.False(cache.Add(new SstBlockCacheKey("sst", iteration), [1]));
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.UsedBytes);
        }
    }

    private static int CountHotSurvivors(PantsBlockCachePolicy policy)
    {
        var cache = new SstBlockCache(policy, capacityBytes: 4, shardCount: 1);
        SstBlockCacheKey[] hot = [new("hot", 0), new("hot", 1)];
        Assert.True(cache.Add(hot[0], [0]));
        Assert.True(cache.Add(hot[1], [1]));
        Assert.True(cache.Add(new SstBlockCacheKey("cold", 0), [2]));
        Assert.True(cache.Add(new SstBlockCacheKey("cold", 1), [3]));
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
            _ = cache.Add(new SstBlockCacheKey("scan", scanBlock), [checked((byte)scanBlock)]);
        }

        return hot.Count(key => cache.TryGet(key, out _));
    }

    private static SstBlockCacheKey FindKeyForShard(SstBlockCache cache, int shard)
    {
        for (var index = 0; index < 10_000; index++)
        {
            var key = new SstBlockCacheKey($"sst-{index}", index);
            if (cache.GetShardIndex(key) == shard)
            {
                return key;
            }
        }

        throw new InvalidOperationException($"Could not find a key for shard {shard}.");
    }
}
