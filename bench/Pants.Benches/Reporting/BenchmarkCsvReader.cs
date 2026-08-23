using System.Globalization;

namespace Cntryl.Pants.Benches.Reporting;

static class BenchmarkCsvReader
{
    static readonly HashSet<string> InfrastructureColumns = new(StringComparer.Ordinal)
    {
        "Method", "Job", "Mean", "Error", "StdDev", "Median", "Ratio", "RatioSD",
        "Gen0", "Gen1", "Gen2", "Allocated"
    };

    public static IReadOnlyList<BenchmarkResult> Read(
        string csv,
        string benchmarkType,
        IReadOnlyDictionary<string, int> operationsPerMethod)
    {
        var tier = TierFromType(benchmarkType);
        var results = CsvTable.Parse(csv).Select(row => ReadRow(row, benchmarkType, tier, operationsPerMethod)).ToArray();
        var duplicate = results.GroupBy(result => result.ScenarioId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Duplicate benchmark scenario '{duplicate.Key}'.");
        }

        return results;
    }

    static BenchmarkResult ReadRow(
        IReadOnlyDictionary<string, string> row,
        string benchmarkType,
        string tier,
        IReadOnlyDictionary<string, int> operationsPerMethod)
    {
        var method = Required(row, "Method");
        var mean = Required(row, "Mean");
        var allocated = Required(row, "Allocated");
        if (!operationsPerMethod.TryGetValue(method, out var operations) || operations <= 0)
        {
            throw new InvalidDataException($"Missing logical operation count for '{method}'.");
        }

        var parameters = row
            .Where(pair => !InfrastructureColumns.Contains(pair.Key) && !IsJobCharacteristic(pair.Key))
            .Where(pair => pair.Value.Length > 0)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}")
            .ToArray();
        var parameterText = string.Join(";", parameters);
        var scenarioId = $"pants:{benchmarkType}:{method}" +
            (parameterText.Length == 0 ? string.Empty : $":{parameterText}");
        var errorRaw = row.GetValueOrDefault("Error");
        double? error = string.IsNullOrWhiteSpace(errorRaw) || errorRaw.Equals("NA", StringComparison.OrdinalIgnoreCase)
            ? null
            : BenchmarkUnitParser.ParseTimeNanoseconds(errorRaw);
        var job = row.GetValueOrDefault("Job") ?? string.Empty;

        return new BenchmarkResult(
            scenarioId,
            tier,
            $"{benchmarkType}.{method}",
            parameterText,
            operations,
            BenchmarkUnitParser.ParseTimeNanoseconds(mean),
            error,
            BenchmarkUnitParser.ParseBytes(allocated),
            job.Equals("Dry", StringComparison.OrdinalIgnoreCase) ? "cold" : "statistical");
    }

    static string Required(IReadOnlyDictionary<string, string> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value) ||
            value.Equals("NA", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Benchmark result is missing required '{key}'.");
        }

        return value;
    }

    static string TierFromType(string benchmarkType)
    {
        var segment = benchmarkType.Split('.').FirstOrDefault(part =>
            part.Length == 5 && part.StartsWith("Tier", StringComparison.Ordinal));
        return segment ?? throw new InvalidDataException($"Cannot derive tier from '{benchmarkType}'.");
    }

    static bool IsJobCharacteristic(string name) => name is
        "AnalyzeLaunchVariance" or "EvaluateOverhead" or "MaxAbsoluteError" or "MaxRelativeError" or
        "MinInvokeCount" or "MinIterationTime" or "OutlierMode" or "Affinity" or
        "EnvironmentVariables" or "Jit" or "LargeAddressAware" or "Platform" or "PowerPlanMode" or
        "Runtime" or "AllowVeryLargeObjects" or "Concurrent" or "CpuGroups" or "Force" or
        "HeapAffinitizeMask" or "HeapCount" or "NoAffinitize" or "RetainVm" or "Server" or
        "Arguments" or "BuildConfiguration" or "Clock" or "EngineFactory" or "NuGetReferences" or
        "Toolchain" or "IsMutator" or "InvocationCount" or "IterationCount" or "IterationTime" or
        "LaunchCount" or "MaxIterationCount" or "MaxWarmupIterationCount" or "MemoryRandomization" or
        "MinIterationCount" or "MinWarmupIterationCount" or "RunStrategy" or "UnrollFactor" or
        "WarmupCount";
}
