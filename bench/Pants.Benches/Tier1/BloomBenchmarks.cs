using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Storage.Internal.Sst;

namespace Cntryl.Pants.Benches.Tier1;

public class BloomBenchmarks : Tier1Benchmark
{
    string _path = null!;
    MidgeSstReader _reader = null!;
    byte[] _hit = null!;
    byte[] _miss = null!;

    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"pants-tier1-{Guid.NewGuid():N}.sst");
        var entries = Enumerable.Range(0, 2_048)
            .Select(index => new MidgeSstEntry(
                BenchmarkData.Key(index),
                BenchmarkData.Value(64),
                checked((ulong)index + 1),
                null,
                false))
            .ToArray();
        File.WriteAllBytes(_path, MidgeSstCodec.Encode(entries, [], PantsPerformanceGoal.Latency));
        _reader = MidgeSstReader.Open(_path);
        _hit = BenchmarkData.Key(1_000);
        _miss = BenchmarkData.Key(1_001_000);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _reader.Dispose();
        File.Delete(_path);
    }

    [Benchmark]
    public int MaybeContainsHit() => _reader.GetPointReadDecision(_hit).CandidateBlockIndex;

    [Benchmark]
    public int MaybeContainsMiss() => _reader.GetPointReadDecision(_miss).CandidateBlockIndex;
}
