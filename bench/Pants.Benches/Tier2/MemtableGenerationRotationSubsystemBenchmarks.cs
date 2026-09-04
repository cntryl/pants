using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Storage.Internal.Flush;
using Cntryl.Pants.Storage.Internal.Wal;

namespace Cntryl.Pants.Tier2;

public class MemtableGenerationRotationSubsystemBenchmarks : Tier2Benchmark
{
    const uint FamilyCount = 32;
    const int OperationsPerFamily = 256;
    List<WalMutation> _legacy = null!;
    WalMutation[] _mutations = null!;
    MutableMemtableOperations _versioned = null!;

    [GlobalSetup]
    public void Setup()
    {
        _mutations = Enumerable.Range(0, checked((int)FamilyCount * OperationsPerFamily))
            .Select(static index => new WalMutation(
                checked((uint)(index % FamilyCount)),
                WalOperation.Put,
                BitConverter.GetBytes(index),
                [0x01],
                checked((ulong)index + 1),
                null,
                null))
            .ToArray();
    }

    [IterationSetup(Target = nameof(LegacyFilterAndRemove))]
    public void SetupLegacy() => _legacy = [.. _mutations];

    [IterationSetup(Target = nameof(DetachGeneration))]
    public void SetupVersioned()
    {
        _versioned = new MutableMemtableOperations();
        _versioned.AddRange(_mutations);
    }

    [Benchmark(Baseline = true)]
    public int LegacyFilterAndRemove()
    {
        var frozen = _legacy
            .Where(static operation => operation.ColumnFamilyId == 0)
            .ToArray();
        _legacy.RemoveAll(static operation => operation.ColumnFamilyId == 0);
        return frozen.Length;
    }

    [Benchmark]
    public int DetachGeneration() => _versioned.DetachFamily(0).Count;
}
