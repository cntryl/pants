using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace Cntryl.Pants.Reporting;

static class BenchmarkInventory
{
    public static IReadOnlyList<string> DiscoverScenarioIds()
    {
        var assembly = typeof(Program).Assembly;
        return assembly.GetTypes()
            .Where(type =>
                !type.IsAbstract && type.GetMethods()
                    .Any(method => method.GetCustomAttribute<BenchmarkAttribute>() is not null))
            .SelectMany(type => BenchmarkConverter.TypeToBenchmarks(type).BenchmarksCases)
            .Select(benchmark => benchmark.Descriptor.Type.FullName + "." + benchmark.Descriptor.WorkloadMethod.Name +
                                 ":" + benchmark.Parameters.DisplayInfo)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
