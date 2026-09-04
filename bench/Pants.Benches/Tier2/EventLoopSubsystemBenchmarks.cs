using System.Threading.Channels;
using BenchmarkDotNet.Attributes;

namespace Cntryl.Pants.Tier2;

public class EventLoopSubsystemBenchmarks : Tier2Benchmark
{
    const int MessageCount = 32_768;
    readonly int _messageCount = MessageCount;

    [Benchmark(OperationsPerInvoke = MessageCount)]
    public async Task<long> CrossThreadDispatchAsync()
    {
        var channel = Channel.CreateBounded<int>(new BoundedChannelOptions(1_024)
        {
            SingleReader = true,
            SingleWriter = true
        });
        var producer = Task.Run(async () =>
        {
            for (var index = 0; index < _messageCount; index++)
            {
                await channel.Writer.WriteAsync(index);
            }

            channel.Writer.Complete();
        });
        long sum = 0;
        await foreach (var value in channel.Reader.ReadAllAsync())
        {
            sum += value;
        }

        await producer;
        return sum;
    }
}
