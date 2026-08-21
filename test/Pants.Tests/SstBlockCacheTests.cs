namespace Pants.Tests;

public sealed class SstBlockCacheTests
{
    [Fact]
    public void ShouldUseRecencyForLruAdmission()
    {
        var cache = new SstBlockCache(PantsBlockCachePolicy.Lru, capacityBytes: 2);
        SstBlockCacheKey hot = new("sst", 0);
        SstBlockCacheKey recent = new("sst", 1);
        SstBlockCacheKey incoming = new("sst", 2);
        cache.Add(hot, 1);
        cache.Add(recent, 1);
        Assert.True(cache.TryGet(hot));
        Assert.True(cache.TryGet(recent));

        cache.Add(incoming, 1);

        Assert.False(cache.TryGet(hot));
        Assert.True(cache.TryGet(recent));
        Assert.True(cache.TryGet(incoming));
    }

    [Fact]
    public void ShouldProtectFrequentlyUsedBlocksWithTinyLfu()
    {
        var cache = new SstBlockCache(PantsBlockCachePolicy.TinyLfu, capacityBytes: 2);
        SstBlockCacheKey hot = new("sst", 0);
        SstBlockCacheKey recent = new("sst", 1);
        SstBlockCacheKey incoming = new("sst", 2);
        cache.Add(hot, 1);
        cache.Add(recent, 1);
        for (int access = 0; access < 8; access++)
        {
            Assert.True(cache.TryGet(hot));
        }

        Assert.True(cache.TryGet(recent));
        cache.Add(incoming, 1);

        Assert.True(cache.TryGet(hot));
        Assert.True(cache.TryGet(recent));
        Assert.False(cache.TryGet(incoming));
    }

    [Fact]
    public void ShouldGiveReferencedBlocksASecondChanceWithClockPro()
    {
        var cache = new SstBlockCache(PantsBlockCachePolicy.ClockPro, capacityBytes: 2);
        SstBlockCacheKey first = new("sst", 0);
        SstBlockCacheKey second = new("sst", 1);
        SstBlockCacheKey incoming = new("sst", 2);
        cache.Add(first, 1);
        cache.Add(second, 1);
        Assert.True(cache.TryGet(first));

        cache.Add(incoming, 1);

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet(incoming));
    }

    [Fact]
    public void ShouldRemoveAllCachedBlocksGivenObsoleteSst()
    {
        var cache = new SstBlockCache(PantsBlockCachePolicy.Lru, capacityBytes: 3);
        cache.Add(new SstBlockCacheKey("obsolete.sst", 0), 1);
        cache.Add(new SstBlockCacheKey("obsolete.sst", 1), 1);
        cache.Add(new SstBlockCacheKey("live.sst", 0), 1);

        cache.RemoveFile("obsolete.sst");

        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet(new SstBlockCacheKey("obsolete.sst", 0)));
        Assert.True(cache.TryGet(new SstBlockCacheKey("live.sst", 0)));
    }
}
