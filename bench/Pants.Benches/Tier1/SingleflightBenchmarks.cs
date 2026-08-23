using BenchmarkDotNet.Attributes;
using Cntryl.Pants.Runtime.Internal;

namespace Cntryl.Pants.Benches.Tier1;

public class SingleflightBenchmarks : Tier1Benchmark
{
    RuntimeResponseSlot<int> _slot = null!;
    Task<int>[] _waiters = null!;

    [Params(1, 4, 16, 64)]
    public int WaiterCount { get; set; }

    [IterationSetup]
    public void Setup()
    {
        _slot = new RuntimeResponseSlot<int>();
        _waiters = Enumerable.Range(0, WaiterCount).Select(_ => _slot.Response).ToArray();
    }

    [Benchmark]
    public async Task CompleteWaiters()
    {
        _slot.Complete(42);
        await Task.WhenAll(_waiters).ConfigureAwait(false);
    }
}
