using BenchmarkDotNet.Attributes;

namespace Cntryl.Pants.Benches.Tier2;

public class DurabilityCommitLatencySubsystemBenchmarks : Tier2Benchmark
{
    const int TransactionCount = 512;
    string _path = null!;
    IPantsDatabase _database = null!;
    byte[][] _keys = null!;
    byte[] _value = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"pants-tier2-durability-{Guid.NewGuid():N}");
        _database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(_path));
        _keys = Enumerable.Range(0, TransactionCount).Select(Tier2Data.Key).ToArray();
        _value = Tier2Data.Value(128);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _database.DisposeAsync();
        Tier2Database.DeletePath(_path);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = TransactionCount)]
    public Task SyncOneWriterAsync() => RunWritersAsync(1);

    [Benchmark(OperationsPerInvoke = TransactionCount)]
    public Task Sync16WritersAsync() => RunWritersAsync(16);

    [Benchmark(OperationsPerInvoke = TransactionCount)]
    public Task Sync64WritersAsync() => RunWritersAsync(64);

    async Task RunWritersAsync(int writers)
    {
        var tasks = Enumerable.Range(0, writers).Select(writer => CommitWriterAsync(writer, writers));
        await Task.WhenAll(tasks);
    }

    async Task CommitWriterAsync(int writer, int writers)
    {
        for (var index = writer; index < TransactionCount; index += writers)
        {
            await Tier2Database.PutAsync(_database, _keys[index], _value, PantsWriteOptions.Sync);
        }
    }
}
