using BenchmarkDotNet.Running;
using Cntryl.Pants.Benches.Reporting;

namespace Cntryl.Pants.Benches;

static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["practical", ..])
        {
            return PracticalBenchmarkRunner.Run(args.Skip(1).ToArray());
        }

        if (args is ["aggregate", ..])
        {
            return BenchmarkReportCommand.Run(args.Skip(1).ToArray());
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
