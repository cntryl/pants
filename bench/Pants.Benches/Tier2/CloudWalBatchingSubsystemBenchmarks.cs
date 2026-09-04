using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Cloud.Internal;
using Cntryl.Pants.Storage.Internal;

namespace Cntryl.Pants.Tier2;

[MemoryDiagnoser]
public class CloudWalBatchingSubsystemBenchmarks : IAsyncDisposable
{
    const int SegmentCount = 8;
    string _path = null!;
    SimulatedCloudPersistence _persistence = null!;
    SealedWalSegment[] _segments = null!;

    public async ValueTask DisposeAsync()
    {
        if (_persistence is not null)
        {
            await _persistence.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    [IterationSetup(Target = nameof(PublishIndividually))]
    public void SetupIndividual() => Setup();

    [IterationSetup(Target = nameof(PublishBatch))]
    public void SetupBatch() => Setup();

    [IterationCleanup]
    public async Task Cleanup()
    {
        await _persistence.DisposeAsync();
        if (Directory.Exists(_path))
        {
            Directory.Delete(_path, true);
        }
    }

    [Benchmark(Baseline = true)]
    public void PublishIndividually()
    {
        foreach (var segment in _segments)
        {
            _persistence.PublishWal(segment);
        }
    }

    [Benchmark]
    public void PublishBatch() => _persistence.PublishWalBatch(_segments);

    void Setup()
    {
        _path = Path.Combine(Path.GetTempPath(), $"pants-cloud-wal-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_path);
        _persistence = new SimulatedCloudPersistence(_path, 1);
        _segments = Enumerable.Range(1, SegmentCount)
            .Select(index => new SealedWalSegment(
                checked((ulong)index),
                1,
                checked((ulong)index),
                $"{index}.wal",
                Tier2Data.Value(4096, index)))
            .ToArray();
    }
}
