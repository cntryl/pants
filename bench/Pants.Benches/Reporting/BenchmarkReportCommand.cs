using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Cntryl.Pants.Benches.Reporting;

static class BenchmarkReportCommand
{
    public static int Run(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: aggregate <pants-artifact-directory> <midge-artifact-directory> <output.md>");
            return 2;
        }

        try
        {
            var pants = ReadPants(args[0]);
            var midge = ReadMidge(args[1]);
            File.WriteAllText(args[2], Render(pants.Metadata, pants.Results, midge));
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    internal static (BenchmarkRunMetadata Metadata, IReadOnlyList<BenchmarkResult> Results) ReadPants(string directory)
    {
        var metadataPath = Path.Combine(directory, "pants-metadata.json");
        var metadata = JsonSerializer.Deserialize<BenchmarkRunMetadata>(File.ReadAllText(metadataPath)) ??
            throw new InvalidDataException("Pants benchmark metadata is invalid.");
        var results = Directory.GetFiles(directory, "*-report.csv", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .SelectMany(path =>
            {
                var benchmarkType = Path.GetFileName(path)[..^"-report.csv".Length];
                return BenchmarkCsvReader.Read(
                    File.ReadAllText(path),
                    benchmarkType,
                    OperationsPerMethod(benchmarkType));
            })
            .ToArray();
        var duplicate = results.GroupBy(result => result.ScenarioId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Duplicate Pants benchmark scenario '{duplicate.Key}'.");
        }

        if (results.Length != metadata.ExpectedScenarioCount)
        {
            throw new InvalidDataException(
                $"Pants benchmark is incomplete: expected {metadata.ExpectedScenarioCount} rows, found {results.Length}.");
        }

        return (metadata, results);
    }

    internal static IReadOnlyList<MidgeBenchmarkResult> ReadMidge(string directory)
    {
        var files = Directory.GetFiles(directory, "latest.json", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidDataException("No Midge latest.json benchmark artifacts were found.");
        }

        var results = files.SelectMany(path => MidgeBenchmarkReader.Read(File.ReadAllText(path))).ToArray();
        var duplicate = results.GroupBy(result => result.ScenarioId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Duplicate Midge benchmark scenario '{duplicate.Key}'.");
        }

        if (results.Select(result => (result.SourceSha, result.Cpu, result.OperatingSystem, result.Runtime, result.ToolVersion))
            .Distinct().Count() != 1)
        {
            throw new InvalidDataException("Midge benchmark artifacts contain inconsistent source or machine metadata.");
        }

        return results;
    }

    internal static string Render(
        BenchmarkRunMetadata metadata,
        IReadOnlyList<BenchmarkResult> pants,
        IReadOnlyList<MidgeBenchmarkResult> midge)
    {
        var output = new StringBuilder();
        output.AppendLine("# Pants and Midge benchmark comparison");
        output.AppendLine();
        output.AppendLine(CultureInfo.InvariantCulture, $"Pants SHA: `{metadata.SourceSha}`  ");
        output.AppendLine(CultureInfo.InvariantCulture, $"Midge SHA: `{MidgeBenchmarkReader.PinnedSourceSha}`  ");
        output.AppendLine(CultureInfo.InvariantCulture, $"Pants machine: {metadata.Cpu}; {metadata.OperatingSystem}; {metadata.Architecture}; {metadata.Runtime}  ");
        var midgeEnvironment = midge[0];
        output.AppendLine(CultureInfo.InvariantCulture, $"Midge machine: {midgeEnvironment.Cpu}; {midgeEnvironment.OperatingSystem}; {midgeEnvironment.Runtime}; cntryl-stress {midgeEnvironment.ToolVersion}");
        output.AppendLine();
        output.AppendLine("> Rows are grouped for investigation, not treated as equivalent. A ratio is intentionally omitted unless workload mechanics, logical units, storage mode, client count, and measurement class are explicitly matched.");

        foreach (var tier in pants.Select(result => result.Tier).Concat(midge.Select(result => result.Tier))
                     .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            output.AppendLine();
            output.AppendLine(CultureInfo.InvariantCulture, $"## {tier}");
            output.AppendLine();
            output.AppendLine("### Pants");
            output.AppendLine();
            output.AppendLine("| Workload | Parameters | Mean ns/op | Allocated B/op | Class |");
            output.AppendLine("|---|---|---:|---:|---|");
            foreach (var result in pants.Where(result => result.Tier == tier).OrderBy(result => result.ScenarioId, StringComparer.Ordinal))
            {
                output.AppendLine(CultureInfo.InvariantCulture, $"| {Escape(result.Workload)} | {Escape(result.Parameters)} | {result.MeanNanoseconds:F2} | {result.AllocatedBytes:F2} | {result.MeasurementClass} |");
            }

            output.AppendLine();
            output.AppendLine("### Midge");
            output.AppendLine();
            output.AppendLine("| Workload | Parameters | Metric | Mean | Quality | Trust |");
            output.AppendLine("|---|---|---|---:|---|---|");
            foreach (var result in midge.Where(result => result.Tier == tier).OrderBy(result => result.ScenarioId, StringComparer.Ordinal))
            {
                output.AppendLine(CultureInfo.InvariantCulture, $"| {Escape(result.Workload)} | {Escape(result.Parameters)} | {result.PrimaryMetric} | {result.Mean:F2} | {result.Quality} | {result.TrustClass} |");
            }
        }

        return output.ToString();
    }

    static Dictionary<string, int> OperationsPerMethod(string benchmarkType)
    {
        var type = typeof(Program).Assembly.GetType(benchmarkType) ??
            throw new InvalidDataException($"Unknown Pants benchmark type '{benchmarkType}'.");
        return type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<BenchmarkAttribute>()))
            .Where(item => item.Attribute is not null)
            .ToDictionary(item => item.Method.Name, item => item.Attribute!.OperationsPerInvoke, StringComparer.Ordinal);
    }

    static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
