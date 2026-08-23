using System.Threading.Channels;
using BenchmarkDotNet.Attributes;

namespace Cntryl.Pants.Benches.Tier1;

public class EventLoopBenchmarks : Tier1Benchmark
{
    Channel<int> _channel = null!;

    [IterationSetup]
    public void Setup() => _channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true,
        AllowSynchronousContinuations = true
    });

    [Benchmark]
    public int Dispatch()
    {
        _channel.Writer.TryWrite(1);
        return _channel.Reader.TryRead(out var value) ? value : 0;
    }

    [Benchmark(OperationsPerInvoke = 100)]
    public int DispatchAndReceive100()
    {
        for (var index = 0; index < 100; index++)
        {
            _channel.Writer.TryWrite(index);
        }

        var sum = 0;
        while (_channel.Reader.TryRead(out var value))
        {
            sum += value;
        }

        return sum;
    }
}
