using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Scan;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Benches.Tier3;

public class ScanSystemBenchmarks : Tier3Benchmark
{
    const int KeysPerFlush = 256;
    string _path = null!;
    IPantsDatabase _database = null!;
    IPantsTransaction _snapshot = null!;

    [Params(Tier3StorageMode.Local, Tier3StorageMode.SimulatedCloud)]
    public Tier3StorageMode StorageMode { get; set; }

    [Params(Tier3ScanLayout.L0Only, Tier3ScanLayout.L0PlusL1, Tier3ScanLayout.FullyCompacted)]
    public Tier3ScanLayout Layout { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"pants-tier3-scan-{Guid.NewGuid():N}");
        _database = await PantsDatabase.OpenAsync(
            Tier3Database.Options(_path, StorageMode).WithBackgroundCompaction(false));
        await WriteFlushAsync(0);
        if (Layout is Tier3ScanLayout.L0PlusL1 or Tier3ScanLayout.FullyCompacted)
        {
            await _database.CompactAllAsync();
        }

        var flushes = Layout switch
        {
            Tier3ScanLayout.L0Only => 1,
            Tier3ScanLayout.L0PlusL1 => 2,
            Tier3ScanLayout.FullyCompacted => 3,
            _ => throw new ArgumentOutOfRangeException()
        };
        for (var batch = 1; batch < flushes; batch++)
        {
            await WriteFlushAsync(batch);
        }

        if (Layout == Tier3ScanLayout.FullyCompacted)
        {
            await _database.CompactAllAsync();
        }

        _snapshot = await _database.BeginTransactionAsync(
            _database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _snapshot.DisposeAsync();
        await _database.DisposeAsync();
        Tier3Database.DeletePath(_path);
    }

    [Benchmark]
    public async Task<byte> PrefixScanFirstRowAsync()
    {
        await using var scan = await _snapshot.ScanAsync(new PantsScanQuery
        {
            Prefix = new byte[] { 0x7a },
            Limit = 1
        });
        await foreach (var entry in scan)
        {
            return entry.Key.Span[0];
        }

        return 0;
    }

    async Task WriteFlushAsync(int batch)
    {
        var entries = Enumerable.Range(0, KeysPerFlush).Select(offset =>
        {
            var ordinal = (batch * KeysPerFlush) + offset;
            return (Tier3Data.Key(ordinal), Tier3Data.Value(64, ordinal));
        });
        await Tier3Database.PutBatchAsync(_database, entries, Tier3Database.WriteOptions(StorageMode));
        await _database.FlushAsync(_database.DefaultColumnFamily);
    }
}
