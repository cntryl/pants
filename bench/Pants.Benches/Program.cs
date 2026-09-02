using BenchmarkDotNet.Running;
using Cntryl.Pants.Benches.Reporting;
using Cntryl.Pants.Benches.Tier4;

namespace Cntryl.Pants.Benches;

static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["practical", ..])
        {
            return PracticalBenchmarkRunner.Run(args.Skip(1).ToArray());
        }

        if (args is ["aggregate", ..])
        {
            return BenchmarkReportCommand.Run(args.Skip(1).ToArray());
        }

        if (args is ["scaleladder", ..])
        {
            return await ScaleLadderRunner.RunAsync(args.Skip(1).ToArray());
        }

        if (args is ["scaleladder-crash-child", var databasePath, var recordCountArg, var readyMarkerPath])
        {
            await ScaleLadderCrashCheck.RunChildAsync(
                databasePath,
                int.Parse(recordCountArg, System.Globalization.CultureInfo.InvariantCulture),
                readyMarkerPath);
            return 0;
        }

        if (args is [
                "scaleladder-reopen-probe-child",
                var probeDatabasePath,
                var probeRecordCountArg,
                var probeBudgetBytesArg,
                var probeResultsPath])
        {
            await ScaleLadderReopenProbe.RunChildAsync(
                probeDatabasePath,
                long.Parse(probeRecordCountArg, System.Globalization.CultureInfo.InvariantCulture),
                long.Parse(probeBudgetBytesArg, System.Globalization.CultureInfo.InvariantCulture),
                probeResultsPath);
            return 0;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
