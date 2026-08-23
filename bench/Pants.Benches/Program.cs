using BenchmarkDotNet.Running;

namespace Cntryl.Pants.Benches;

static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
