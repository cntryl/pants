using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Storage;
using Cntryl.Pants.Storage.Internal.Cache;

namespace Cntryl.Pants.Tier1;

public class BlockCacheBenchmarks : Tier1Benchmark
{
    readonly byte[] _block = BenchmarkData.Value(4 * 1024);
    SstBlockCache _cache = null!;
    SstBlockCacheKey _hitKey;
    SstBlockCacheKey _missKey;
    int _nextBlock;
    SstBlockCacheKey[] _writeKeys = null!;

    [GlobalSetup]
    public void Setup()
    {
        _cache = new SstBlockCache(PantsBlockCachePolicy.Lru, 16 * 1024 * 1024);
        _hitKey = new SstBlockCacheKey("hot.sst", 0);
        _missKey = new SstBlockCacheKey("cold.sst", 0);
        _cache.Add(_hitKey, _block);
        _writeKeys = Enumerable.Range(0, 1_024)
            .Select(index => new SstBlockCacheKey("insert.sst", index))
            .ToArray();
    }

    [Benchmark]
    public bool GetHot4K() => _cache.TryGet(_hitKey, out _);

    [Benchmark]
    public bool GetMiss4K() => _cache.TryGet(_missKey, out _);

    [Benchmark]
    public bool Insert4K() => _cache.Add(
        _writeKeys[unchecked((uint)Interlocked.Increment(ref _nextBlock) % (uint)_writeKeys.Length)],
        _block);
}
