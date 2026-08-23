using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Storage.Internal.Sst.Trie;

namespace Cntryl.Pants.Benches.Tier1;

public class TrieBenchmarks : Tier1Benchmark
{
    IReadOnlyList<byte[]> _keys = null!;
    byte[] _encoded = null!;
    MidgeTrieIndex _trie = null!;
    byte[] _hit = null!;
    byte[] _miss = null!;

    [GlobalSetup]
    public void Setup()
    {
        _keys = Enumerable.Range(0, 1_024).Select(BenchmarkData.Key).ToArray();
        _encoded = MidgeTrieIndex.Encode(_keys);
        _trie = MidgeTrieIndex.Decode(_encoded, _keys);
        _hit = BenchmarkData.Key(512);
        _miss = BenchmarkData.Key(2_000);
    }

    [Benchmark]
    public int FindHit() => _trie.FindFloorBlock(_hit);

    [Benchmark]
    public int FindMiss() => _trie.FindFloorBlock(_miss);

    [Benchmark(OperationsPerInvoke = 1_024)]
    public byte[] Encode1024() => MidgeTrieIndex.Encode(_keys);

    [Benchmark(OperationsPerInvoke = 1_024)]
    public object Decode1024() => MidgeTrieIndex.Decode(_encoded, _keys);
}
