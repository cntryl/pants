using BenchmarkDotNet.Attributes;

namespace Cntryl.Pants.Tier2;

public class ReadAmplificationSubsystemBenchmarks : Tier2Benchmark
{
    const int LookupCount = 1_000;
    IPantsDatabase _database = null!;
    string _path = null!;
    byte[][] _skewedKeys = null!;
    byte[][] _uniformKeys = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"pants-tier2-read-{Guid.NewGuid():N}");
        _database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(_path).WithBackgroundCompaction(false));
        for (var run = 0; run < 4; run++)
        {
            for (var index = 0; index < 10_000; index++)
            {
                await Tier2Database.PutAsync(
                    _database,
                    Tier2Data.Key(index),
                    Tier2Data.Value(64, run + index),
                    PantsWriteOptions.Buffered);
            }

            await _database.Maintenance.FlushAsync(_database.ColumnFamilies.DefaultFamily);
        }

        _uniformKeys = Enumerable.Range(0, LookupCount).Select(index => Tier2Data.Key(index * 7919 % 10_000)).ToArray();
        _skewedKeys = Enumerable.Range(0, LookupCount).Select(index => Tier2Data.Key(index % 100)).ToArray();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _database.DisposeAsync();
        Tier2Database.DeletePath(_path);
    }

    [Benchmark(OperationsPerInvoke = LookupCount)]
    public Task<int> UniformPointReadsAsync() => ReadAsync(_uniformKeys);

    [Benchmark(OperationsPerInvoke = LookupCount)]
    public Task<int> SkewedPointReadsAsync() => ReadAsync(_skewedKeys);

    async Task<int> ReadAsync(byte[][] keys)
    {
        var found = 0;
        foreach (var key in keys)
        {
            if (await Tier2Database.GetAsync(_database, key) is not null)
            {
                found++;
            }
        }

        return found;
    }
}
