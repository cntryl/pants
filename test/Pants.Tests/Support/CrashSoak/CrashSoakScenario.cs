using System.Globalization;

namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>
///     The engine configuration and key space one seed drives. Every field is derived from the seed,
///     so replaying the seed replays the same configuration, workload, and kill points.
/// </summary>
sealed record CrashSoakScenario(
    int Seed,
    CrashSoakBackend Backend,
    int OperationsPerCycle,
    int LaneCount,
    int KeysPerLane,
    bool BackgroundCompaction,
    int FlushAfterWalRecords,
    long MemtableFlushThresholdBytes)
{
    public static CrashSoakScenario FromSeed(int seed, CrashSoakBackend backend, int operationsPerCycle)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(operationsPerCycle, 4);
        var random = new Random(seed);
        return new CrashSoakScenario(
            seed,
            backend,
            operationsPerCycle,
            1 + random.Next(4),
            8 + random.Next(17),
            random.Next(2) == 0,
            random.Next(3) == 0 ? 0 : 8 + random.Next(40),
            (16 + random.Next(112)) * 1_024L);
    }

    public static string KeyFor(int lane, int index) =>
        string.Create(CultureInfo.InvariantCulture, $"l{lane}/k{index:000}");

    /// <summary>The fixed per-lane keys every cycle reads and writes; inserted keys are added per cycle.</summary>
    public IEnumerable<string> LaneKeys =>
        Enumerable.Range(0, LaneCount)
            .SelectMany(lane => Enumerable.Range(0, KeysPerLane).Select(index => KeyFor(lane, index)));

    public override string ToString() =>
        $"seed={Seed} backend={Backend} lanes={LaneCount} keysPerLane={KeysPerLane} " +
        $"operationsPerCycle={OperationsPerCycle} backgroundCompaction={BackgroundCompaction} " +
        $"flushAfterWalRecords={FlushAfterWalRecords} memtableFlushThreshold={MemtableFlushThresholdBytes}";
}
