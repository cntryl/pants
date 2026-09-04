using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Storage;
using Cntryl.Pants.Storage.Internal.Cache;

namespace Cntryl.Pants.Tier2;

public class RangeScanCacheSubsystemBenchmarks : Tier2Benchmark
{
    const int BlockCount = 256;
    readonly byte[] _block = Tier2Data.Value(4 * 1024);
    SstBlockCache _coldCache = null!;
    SstBlockCacheKey[] _keys = null!;
    SstBlockCache _warmCache = null!;

    [IterationSetup]
    public void Setup()
    {
        _coldCache = new SstBlockCache(PantsBlockCachePolicy.Lru, 4 * 1024 * 1024);
        _warmCache = new SstBlockCache(PantsBlockCachePolicy.Lru, 4 * 1024 * 1024);
        _keys = Enumerable.Range(0, BlockCount).Select(index => new SstBlockCacheKey("scan.sst", index)).ToArray();
        foreach (var key in _keys)
        {
            _warmCache.Add(key, _block);
        }
    }

    [Benchmark(OperationsPerInvoke = BlockCount)]
    public int WarmSequentialScan() => Scan(_warmCache);

    [Benchmark(OperationsPerInvoke = BlockCount)]
    public int ColdSequentialScan() => Scan(_coldCache);

    int Scan(SstBlockCache cache)
    {
        var hits = 0;
        foreach (var key in _keys)
        {
            if (cache.TryGet(key, out _))
            {
                hits++;
            }
            else
            {
                cache.Add(key, _block);
            }
        }

        return hits;
    }
}
