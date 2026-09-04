using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Storage.Internal.Sst.Trie;

namespace Cntryl.Pants.Tier1;

public class TrieBenchmarks : Tier1Benchmark
{
    byte[] _encoded = null!;
    byte[] _hit = null!;
    IReadOnlyList<byte[]> _keys = null!;
    byte[] _miss = null!;
    TrieIndex _trie = null!;

    [GlobalSetup]
    public void Setup()
    {
        _keys = Enumerable.Range(0, 1_024).Select(BenchmarkData.Key).ToArray();
        _encoded = TrieIndex.Encode(_keys);
        _trie = TrieIndex.Decode(_encoded, _keys);
        _hit = BenchmarkData.Key(512);
        _miss = BenchmarkData.Key(2_000);
    }

    [Benchmark]
    public int FindHit() => _trie.FindFloorBlock(_hit);

    [Benchmark]
    public int FindMiss() => _trie.FindFloorBlock(_miss);

    [Benchmark(OperationsPerInvoke = 1_024)]
    public byte[] Encode1024() => TrieIndex.Encode(_keys);

    [Benchmark(OperationsPerInvoke = 1_024)]
    public object Decode1024() => TrieIndex.Decode(_encoded, _keys);
}
