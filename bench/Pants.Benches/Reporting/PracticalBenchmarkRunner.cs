using System.Text.Json;
using BenchmarkDotNet.Running;

namespace Cntryl.Pants.Reporting;

static class PracticalBenchmarkRunner
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static int Run(string[] args)
    {
        var artifactPath = args.Length == 0
            ? Path.Combine("BenchmarkDotNet.Artifacts", "practical")
            : args[0];
        if (args.Length > 1)
        {
            Console.Error.WriteLine("Usage: practical [artifact-directory]");
            return 2;
        }

        var scenarios = BenchmarkInventory.DiscoverScenarioIds();
        if (scenarios.Count != 157 || scenarios.Distinct(StringComparer.Ordinal).Count() != scenarios.Count)
        {
            Console.Error.WriteLine(
                $"Practical inventory must contain 157 unique scenarios; discovered {scenarios.Count}.");
            return 1;
        }

        Directory.CreateDirectory(artifactPath);
        var metadata = BenchmarkEnvironment.Capture("cold", scenarios.Count);
        File.WriteAllText(
            Path.Combine(artifactPath, "pants-metadata.json"),
            JsonSerializer.Serialize(metadata, JsonOptions));
        File.WriteAllLines(Path.Combine(artifactPath, "pants-scenarios.txt"), scenarios);

        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(
        [
            "--job", "Dry",
            "--artifacts", artifactPath,
            "--filter", "*"
        ]);
        var failed = summaries.Any(summary =>
            summary.HasCriticalValidationErrors || summary.Reports.Any(report => !report.Success));
        return failed ? 1 : 0;
    }
}
