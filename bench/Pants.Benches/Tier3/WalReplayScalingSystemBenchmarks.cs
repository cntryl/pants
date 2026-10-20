using System.Globalization;
using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Runtime.Internal;
using Cntryl.Pants.Storage;
using Cntryl.Pants.Transactions;

namespace Cntryl.Pants.Tier3;

public class WalReplayScalingSystemBenchmarks : Tier3Benchmark
{
    readonly List<StartupPhaseMeasurement> _startupMeasurements = [];
    IPantsDatabase? _reopened;
    string _databasePath = null!;
    string _phaseResultsPath = null!;

    [Params(1, 64)]
    public int FlushedSstCount { get; set; }

    [Params(4, 16, 64)]
    public int WalCommitCount { get; set; }

    [GlobalSetup]
    public void SetupResults()
    {
        _phaseResultsPath = Path.Combine(
            Path.GetTempPath(),
            $"pants-wal-replay-scaling-{FlushedSstCount}-{WalCommitCount}-{Guid.NewGuid():N}.csv");
        File.WriteAllText(
            _phaseResultsPath,
            "flushed_sst_count,wal_commit_count,wal_records_replayed,wal_bytes_replayed," +
            "sst_hydration_us,wal_replay_us,wal_replay_allocated_bytes" + Environment.NewLine);
        Console.WriteLine($"WAL replay phase measurements: {_phaseResultsPath}");
    }

    [IterationSetup]
    public void CreateFixture() => CreateFixtureAsync().GetAwaiter().GetResult();

    async Task CreateFixtureAsync()
    {
        _startupMeasurements.Clear();
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"pants-tier3-wal-replay-{Guid.NewGuid():N}");
        var options = Options(_databasePath);

        await using var database = await PantsDatabase.OpenAsync(options);
        for (var index = 0; index < FlushedSstCount; index++)
        {
            await Tier3Database.PutBatchAsync(
                database,
                [(Tier3Data.Key(index), Tier3Data.Value(64, index))],
                PantsWriteOptions.Buffered);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        var runtime = await database.Diagnostics.GetRuntimeMetricsAsync();
        if (runtime.SstCount != FlushedSstCount)
        {
            throw new InvalidOperationException(
                $"Expected {FlushedSstCount} unrelated SSTs, found {runtime.SstCount}.");
        }

        for (var index = 0; index < WalCommitCount; index++)
        {
            await Tier3Database.PutBatchAsync(
                database,
                [(Tier3Data.Key(100_000 + index), Tier3Data.Value(64, index))],
                PantsWriteOptions.Buffered);
        }
    }

    [Benchmark]
    public async Task OpenAndReplayWalAsync()
    {
        _reopened = await PantsDatabase.OpenForTestingAsync(
            Options(_databasePath),
            new RuntimeDependencies(startupPhaseMeasurement: _startupMeasurements.Add));
    }

    [IterationCleanup]
    public void CleanupIteration() => CleanupIterationAsync().GetAwaiter().GetResult();

    async Task CleanupIterationAsync()
    {
        try
        {
            if (_reopened is { } reopened)
            {
                try
                {
                    var recovery = await reopened.Diagnostics.GetRecoveryMetricsAsync();
                    if (recovery.WalRecordsReplayed != WalCommitCount || recovery.WalBytesReplayed <= 0)
                    {
                        throw new InvalidOperationException(
                            $"Expected {WalCommitCount} replayed WAL records; found " +
                            $"{recovery.WalRecordsReplayed} records and {recovery.WalBytesReplayed} bytes.");
                    }

                    if (await Tier3Database.GetAsync(reopened, Tier3Data.Key(0)) is null ||
                        await Tier3Database.GetAsync(reopened, Tier3Data.Key(100_000 + WalCommitCount - 1)) is null)
                    {
                        throw new InvalidOperationException(
                            "The SST or WAL fixture did not recover its boundary keys.");
                    }

                    var sstHydration = _startupMeasurements.Single(
                        static measurement => measurement.Phase == StartupPhase.SstHydration);
                    var walReplay = _startupMeasurements.Single(
                        static measurement => measurement.Phase == StartupPhase.WalReplay);
                    var row = string.Join(",",
                        FlushedSstCount.ToString(CultureInfo.InvariantCulture),
                        WalCommitCount.ToString(CultureInfo.InvariantCulture),
                        recovery.WalRecordsReplayed.ToString(CultureInfo.InvariantCulture),
                        recovery.WalBytesReplayed.ToString(CultureInfo.InvariantCulture),
                        sstHydration.Elapsed.TotalMicroseconds.ToString("F1", CultureInfo.InvariantCulture),
                        walReplay.Elapsed.TotalMicroseconds.ToString("F1", CultureInfo.InvariantCulture),
                        walReplay.AllocatedBytes.ToString(CultureInfo.InvariantCulture));
                    File.AppendAllText(_phaseResultsPath, row + Environment.NewLine);
                }
                finally
                {
                    await reopened.DisposeAsync();
                    _reopened = null;
                }
            }
        }
        finally
        {
            Tier3Database.DeletePath(_databasePath);
        }
    }

    static PantsOpenOptions Options(string path) => PantsOpenOptions.Local(path)
        .WithCompaction(new PantsCompactionConfiguration(
            L0SizeTriggerBytes: long.MaxValue,
            L0FileCountTrigger: 128,
            BackgroundEnabled: false));
}
