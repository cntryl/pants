using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Storage;
using Cntryl.Pants.Storage.Internal.Cache;

namespace Cntryl.Pants.Benches.Tier2;

public class BlockCacheSubsystemBenchmarks : Tier2Benchmark
{
    const int Operations = 10_000;
    readonly byte[] _block = Tier2Data.Value(4 * 1024);
    SstBlockCache _cache = null!;
    SstBlockCacheKey[] _keys = null!;

    [IterationSetup]
    public void Setup()
    {
        _cache = new SstBlockCache(PantsBlockCachePolicy.Lru, 2 * 1024 * 1024);
        _keys = Enumerable.Range(0, Operations).Select(index => new SstBlockCacheKey("cache.sst", index)).ToArray();
        for (var index = 0; index < 500; index++)
        {
            _cache.Add(_keys[index], _block);
        }
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    public int RotateHotSet()
    {
        var hits = 0;
        for (var index = 0; index < Operations; index++)
        {
            var key = _keys[index % 750];
            if (_cache.TryGet(key, out _))
            {
                hits++;
            }
            else
            {
                _cache.Add(key, _block);
            }
        }

        return hits;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    public void EvictionPressure()
    {
        for (var index = 0; index < Operations; index++)
        {
            _cache.Add(_keys[index], _block);
        }
    }
}
