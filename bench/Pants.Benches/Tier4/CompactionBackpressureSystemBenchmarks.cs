using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Exceptions;

namespace Cntryl.Pants.Benches.Tier4;

public class CompactionBackpressureSystemBenchmarks : Tier4Benchmark
{
    const int BatchSize = 64;
    const int BatchCount = 256;
    string _path = null!;
    IPantsDatabase _database = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"pants-tier4-backpressure-{Guid.NewGuid():N}");
        _database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(_path).WithMemtableLimits(512 * 1024));
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _database.DisposeAsync();
        Tier4Database.DeletePath(_path);
    }

    [Benchmark(OperationsPerInvoke = BatchSize * BatchCount)]
    public async Task WriteWhileCompactingAsync()
    {
        var compaction = _database.CompactAllAsync().AsTask();
        for (var batch = 0; batch < BatchCount; batch++)
        {
            var entries = Enumerable.Range(0, BatchSize).Select(offset =>
            {
                var index = batch * BatchSize + offset;
                return (Tier4Data.Key(index), Tier4Data.Value(512, index));
            });
            while (true)
            {
                try
                {
                    await Tier4Database.PutBatchAsync(_database, entries, PantsWriteOptions.Buffered);
                    break;
                }
                catch (PantsWriteStallException)
                {
                    await _database.WaitForWriteStallClearAsync(
                        _database.DefaultColumnFamily,
                        TimeSpan.FromSeconds(10));
                }
            }
        }

        await compaction;
        await _database.WaitForWriteStallClearAsync(_database.DefaultColumnFamily, TimeSpan.FromSeconds(10));
    }
}
