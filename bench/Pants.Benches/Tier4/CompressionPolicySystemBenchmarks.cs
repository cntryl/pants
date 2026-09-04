using BenchmarkDotNet.Attributes;

namespace Cntryl.Pants.Tier4;

public class CompressionPolicySystemBenchmarks : Tier4Benchmark
{
    const int Flushes = 4;
    const int RecordsPerFlush = 128;
    const int ValueSize = 16 * 1024;
    IPantsDatabase _database = null!;
    (byte[] Key, byte[] Value)[][] _flushes = null!;
    string _path = null!;

    [ParamsAllValues] public CompressionGoal Goal { get; set; }

    [ParamsAllValues] public CompressionShape Shape { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"pants-tier4-compression-{Guid.NewGuid():N}");
        var performanceGoal = Goal switch
        {
            CompressionGoal.Latency => PantsPerformanceGoal.Latency,
            CompressionGoal.Throughput => PantsPerformanceGoal.Throughput,
            CompressionGoal.Economy => PantsPerformanceGoal.Economy,
            _ => throw new ArgumentOutOfRangeException()
        };
        _database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(_path).WithPerformanceGoal(performanceGoal));
        _flushes = Enumerable.Range(0, Flushes).Select(flush => Enumerable.Range(0, RecordsPerFlush)
            .Select(offset =>
            {
                var index = flush * RecordsPerFlush + offset;
                return (Tier4Data.Key(index), Value(index));
            }).ToArray()).ToArray();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _database.DisposeAsync();
        Tier4Database.DeletePath(_path);
    }

    [Benchmark(OperationsPerInvoke = Flushes * RecordsPerFlush)]
    public async Task FourFlushesAndCompactionAsync()
    {
        foreach (var flush in _flushes)
        {
            await Tier4Database.PutBatchAsync(_database, flush, PantsWriteOptions.Buffered);
            await _database.Maintenance.FlushAsync(_database.ColumnFamilies.DefaultFamily);
        }

        await _database.Maintenance.CompactAllAsync();
    }

    byte[] Value(int index)
    {
        var value = Tier4Data.Value(ValueSize, index);
        if (Shape is CompressionShape.Structured or CompressionShape.PrefixRandomTail)
        {
            value.AsSpan(0, ValueSize / 2).Fill(0x41);
        }
        else if (Shape == CompressionShape.LowCardinality)
        {
            value.AsSpan().Fill(checked((byte)(index % 4)));
        }
        else if (Shape == CompressionShape.Mixed && index % 2 == 0)
        {
            value.AsSpan().Fill(0x5a);
        }

        return value;
    }
}
