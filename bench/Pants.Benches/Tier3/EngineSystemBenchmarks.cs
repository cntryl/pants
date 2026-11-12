using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Observability;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Tier3;

public class EngineSystemBenchmarks : Tier3Benchmark
{
    const int KeyCount = 4_096;
    const int PutBatchSize = 64;
    const int MemtableSizeBytes = 2 * 1024 * 1024;
    IPantsDatabase _database = null!;
    readonly byte[] _expected = Tier3Data.Value(128, 1);
    IPantsColumnFamily _family = null!;
    byte[][] _keys = null!;
    int _nextKey;
    string _path = null!;
    PantsReadPathDiagnostics _readPathBefore = null!;

    [Params(Tier3StorageMode.Local, Tier3StorageMode.SimulatedCloud)]
    public Tier3StorageMode StorageMode { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"pants-tier3-engine-{Guid.NewGuid():N}");
        _database = await PantsDatabase.OpenAsync(Tier3Database.Options(_path, StorageMode)
            .WithBackgroundCompaction(false)
            .WithMemtableLimits(MemtableSizeBytes));
        _family = await _database.ColumnFamilies.CreateAsync("cf1");
        _keys = Enumerable.Range(0, KeyCount).Select(index =>
        {
            var key = new byte[16];
            BinaryPrimitives.WriteUInt64BigEndian(key, checked((ulong)index));
            return key;
        }).ToArray();
        foreach (var chunk in _keys.Chunk(PutBatchSize))
        {
            await using var transaction = await _database.Transactions.BeginAsync(
                _family,
                PantsTransactionMode.ReadWrite);
            foreach (var key in chunk)
            {
                transaction.Put(key, _expected);
            }

            await transaction.CommitAsync(PantsWriteOptions.BestEffort);
        }

        await _database.Maintenance.FlushAsync(_family);
        _readPathBefore = await _database.Diagnostics.GetReadPathDiagnosticsAsync();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        var readPathAfter = await _database.Diagnostics.GetReadPathDiagnosticsAsync();
        await _database.DisposeAsync();
        Tier3Database.DeletePath(_path);
        if (readPathAfter.ReadOnlyTransactionsBegun <= _readPathBefore.ReadOnlyTransactionsBegun ||
            readPathAfter.CandidateSstFilesChecked <= _readPathBefore.CandidateSstFilesChecked ||
            readPathAfter.CandidateBlocksChecked <= _readPathBefore.CandidateBlocksChecked)
        {
            throw new InvalidOperationException("Measured engine point reads did not exercise the SST read path.");
        }
    }

    [Benchmark]
    public async ValueTask<ReadOnlyMemory<byte>?> GetAsync()
    {
        var index = unchecked((uint)_nextKey++ % KeyCount);
        await using var transaction = await _database.Transactions.BeginAsync(
            _family,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(_keys[index]);
        if (value is not { } found || !found.Span.SequenceEqual(_expected))
        {
            throw new InvalidOperationException("Measured engine point read returned an unexpected value.");
        }

        return value;
    }
}
