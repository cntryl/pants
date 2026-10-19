using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cntryl.Pants.Reporting;

static class MidgeBenchmarkReader
{
    public const string PinnedSourceSha = "c5ffc2d3284c76b6f7cd03444a5b0a38ae8bbc33";
    public const string PinnedStressHarnessSha = "6b7bd34b495f843826eb873e45b7a70b341c74e3";
    public const string PinnedToolVersion = "0.3.0";
    public const string PinnedCargoLockSha256 =
        "484a1b98e6760d680397fcb5be00accad37f6dade8946e2bfe6dfcc517db9b2d";
    public const string PinnedBenchmarkFixturePatchSha256 =
        "f9b0c0035989a5bf98df9377295b9ea8d9efd9edaefdade404f2d517e2ea8b44";

    const string ScenarioInventoryResourceName = "Cntryl.Pants.Benches.Reporting.midge-c5-scenarios.txt";
    static readonly byte[] ScenarioInventoryBytes = ReadScenarioInventoryBytes();
    static readonly HashSet<string> ExpectedScenarioIds = ParseScenarioInventory(ScenarioInventoryBytes);

    public static string PinnedScenarioInventorySha256 =>
        Convert.ToHexString(SHA256.HashData(ScenarioInventoryBytes)).ToLowerInvariant();

    internal static string ValidateCargoLock(byte[] cargoLockBytes)
    {
        var cargoLockSha = Convert.ToHexString(SHA256.HashData(cargoLockBytes)).ToLowerInvariant();
        if (!cargoLockSha.Equals(PinnedCargoLockSha256, StringComparison.Ordinal) ||
            !Encoding.UTF8.GetString(cargoLockBytes).Contains(PinnedStressHarnessSha, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Midge Cargo.lock SHA '{cargoLockSha}' or stress harness revision does not match the pinned lock.");
        }

        return cargoLockSha;
    }

    public static IReadOnlyList<MidgeBenchmarkResult> Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (RequiredString(root, "schema_version") != "cntryl-stress.v2")
        {
            throw new InvalidDataException("The Midge benchmark uses an unsupported stress schema.");
        }

        var environment = Required(root, "environment");
        var sourceSha = RequiredString(environment, "git_commit");
        if (!sourceSha.Equals(PinnedSourceSha, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Midge benchmark SHA '{sourceSha}' does not match pinned SHA '{PinnedSourceSha}'.");
        }

        var cpu = RequiredString(environment, "cpu_model");
        var operatingSystem = RequiredString(environment, "os");
        var runtime = RequiredString(environment, "rustc_version");
        var runProfile = RequiredString(root, "run_profile");
        var toolVersion = RequiredString(root, "tool_version");
        if (!toolVersion.Equals(PinnedToolVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Midge stress tool version '{toolVersion}' does not match pinned version '{PinnedToolVersion}'.");
        }
        var summaries = Required(root, "summaries");
        if (summaries.ValueKind != JsonValueKind.Array || summaries.GetArrayLength() == 0)
        {
            throw new InvalidDataException("The Midge benchmark has no summary rows.");
        }

        var measuredOperations = ReadMeasuredOperations(Required(root, "samples"));
        var results = summaries.EnumerateArray().Select(summary =>
            ReadSummary(summary, measuredOperations, sourceSha, cpu, operatingSystem, runtime, runProfile,
                toolVersion)).ToArray();
        var duplicate = results.GroupBy(result => result.ScenarioId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Duplicate Midge benchmark scenario '{duplicate.Key}'.");
        }

        if (measuredOperations.Count != 0)
        {
            throw new InvalidDataException(
                "Midge benchmark contains measured samples without summaries: " +
                $"[{string.Join(", ", measuredOperations.Keys.Order(StringComparer.Ordinal))}].");
        }

        return results;
    }

    static Dictionary<string, (int Count, ulong Operations)> ReadMeasuredOperations(JsonElement samples)
    {
        if (samples.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Midge benchmark samples must be an array.");
        }

        var totals = new Dictionary<string, (int Count, ulong Operations)>(StringComparer.Ordinal);
        foreach (var sample in samples.EnumerateArray())
        {
            var benchmarkId = RequiredString(sample, "benchmark_id");
            var phase = RequiredString(sample, "phase");
            if (phase is not "warmup" and not "measured" and not "cooldown")
            {
                throw new InvalidDataException($"Midge benchmark sample has unsupported phase '{phase}'.");
            }

            var completedElement = Required(sample, "operations_completed");
            if (completedElement.ValueKind != JsonValueKind.Number ||
                !completedElement.TryGetUInt64(out var completed))
            {
                throw new InvalidDataException(
                    "Midge benchmark sample operations_completed must be a non-negative integer.");
            }

            if (phase != "measured")
            {
                continue;
            }

            if (completed == 0)
            {
                throw new InvalidDataException(
                    $"Midge benchmark '{benchmarkId}' has a measured sample with no completed operations.");
            }

            if (totals.TryGetValue(benchmarkId, out var current))
            {
                try
                {
                    totals[benchmarkId] = (checked(current.Count + 1), checked(current.Operations + completed));
                }
                catch (OverflowException exception)
                {
                    throw new InvalidDataException(
                        $"Midge benchmark '{benchmarkId}' measured-operation totals overflow.", exception);
                }
            }
            else
            {
                totals.Add(benchmarkId, (1, completed));
            }
        }

        return totals;
    }

    static MidgeBenchmarkResult ReadSummary(
        JsonElement summary,
        Dictionary<string, (int Count, ulong Operations)> measuredOperations,
        string sourceSha,
        string cpu,
        string operatingSystem,
        string runtime,
        string runProfile,
        string toolVersion)
    {
        var correctness = Required(summary, "correctness");
        if (!Required(correctness, "passed").GetBoolean())
        {
            throw new InvalidDataException(
                $"Midge benchmark '{RequiredString(summary, "benchmark_id")}' failed correctness validation.");
        }

        var stats = Required(summary, "stats");
        if (stats.ValueKind != JsonValueKind.Object ||
            !stats.TryGetProperty("mean", out var meanElement) ||
            !meanElement.TryGetDouble(out var mean) || !double.IsFinite(mean))
        {
            throw new InvalidDataException(
                $"Midge benchmark '{RequiredString(summary, "benchmark_id")}' has no finite mean.");
        }

        var parameters = Required(summary, "parameters");
        var parameterText = parameters.ValueKind == JsonValueKind.Object
            ? string.Join(";", parameters.EnumerateObject()
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => $"{property.Name}={property.Value.ToString()}"))
            : throw new InvalidDataException("Midge benchmark parameters must be an object.");
        var id = RequiredString(summary, "benchmark_id");
        var name = RequiredString(summary, "name");
        ValidateFixtureMemtable(name, parameters);
        var tier = Required(summary, "tier").GetUInt32();
        var measuredElement = Required(summary, "measured_samples");
        if (measuredElement.ValueKind != JsonValueKind.Number || !measuredElement.TryGetInt32(out var expectedCount) ||
            expectedCount <= 0 || !measuredOperations.Remove(id, out var operations))
        {
            throw new InvalidDataException($"Midge benchmark '{id}' has no measured operation samples.");
        }

        if (operations.Count != expectedCount)
        {
            throw new InvalidDataException(
                $"Midge benchmark '{id}' summary reports {expectedCount} measured samples, " +
                $"but the artifact contains {operations.Count}.");
        }

        return new MidgeBenchmarkResult(
            $"midge:{id}",
            $"Tier{tier.ToString(CultureInfo.InvariantCulture)}",
            name,
            parameterText,
            RequiredString(summary, "primary_metric"),
            expectedCount,
            operations.Operations,
            mean,
            RequiredString(summary, "quality"),
            RequiredString(summary, "trust_class"),
            sourceSha,
            cpu,
            operatingSystem,
            runtime,
            runProfile,
            toolVersion);
    }

    static void ValidateFixtureMemtable(string benchmarkName, JsonElement parameters)
    {
        string? expectedBytes = null;
        if (benchmarkName.StartsWith("tier3_engine_get_", StringComparison.Ordinal) ||
            benchmarkName.StartsWith("tier4_recovery_reopen_after_", StringComparison.Ordinal))
        {
            expectedBytes = "2097152";
        }
        else if (benchmarkName.StartsWith("tier3_sst_point_seek_", StringComparison.Ordinal) ||
                 benchmarkName.StartsWith("tier3_sst_range_seek_", StringComparison.Ordinal))
        {
            expectedBytes = "4194304";
        }

        if (expectedBytes is null)
        {
            return;
        }

        if (!parameters.TryGetProperty("fixture_memtable_size_bytes", out var fixtureBytes) ||
            !fixtureBytes.ToString().Equals(expectedBytes, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Midge benchmark '{benchmarkName}' is missing its pinned fixture memtable marker " +
                $"'{expectedBytes}'.");
        }
    }

    static byte[] ReadScenarioInventoryBytes()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ScenarioInventoryResourceName) ??
            throw new InvalidDataException(
                $"Pinned Midge scenario inventory resource '{ScenarioInventoryResourceName}' is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    static HashSet<string> ParseScenarioInventory(byte[] bytes)
    {
        var ids = Encoding.UTF8.GetString(bytes)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        if (ids.Length == 0 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
        {
            throw new InvalidDataException("Pinned Midge scenario inventory is empty or contains duplicate IDs.");
        }

        return ids.ToHashSet(StringComparer.Ordinal);
    }

    internal static void ValidateScenarioInventory(IEnumerable<string> scenarioIds)
    {
        var actual = scenarioIds.ToHashSet(StringComparer.Ordinal);
        var missing = ExpectedScenarioIds.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var unexpected = actual.Except(ExpectedScenarioIds, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length != 0 || unexpected.Length != 0)
        {
            throw new InvalidDataException(
                "Midge benchmark scenarios do not match the pinned inventory. " +
                $"Missing: [{string.Join(", ", missing)}]; unexpected: [{string.Join(", ", unexpected)}].");
        }
    }

    static JsonElement Required(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) &&
        value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? value
            : throw new InvalidDataException($"Midge benchmark is missing required '{propertyName}'.");

    static string RequiredString(JsonElement parent, string propertyName)
    {
        var value = Required(parent, propertyName);
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Midge benchmark '{propertyName}' must be a non-empty string.");
    }
}
