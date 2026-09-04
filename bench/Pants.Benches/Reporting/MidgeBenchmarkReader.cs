using System.Globalization;
using System.Text.Json;

namespace Cntryl.Pants.Reporting;

static class MidgeBenchmarkReader
{
    public const string PinnedSourceSha = "c5ffc2d3284c76b6f7cd03444a5b0a38ae8bbc33";

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
        var toolVersion = RequiredString(root, "tool_version");
        var summaries = Required(root, "summaries");
        if (summaries.ValueKind != JsonValueKind.Array || summaries.GetArrayLength() == 0)
        {
            throw new InvalidDataException("The Midge benchmark has no summary rows.");
        }

        var results = summaries.EnumerateArray().Select(summary =>
            ReadSummary(summary, sourceSha, cpu, operatingSystem, runtime, toolVersion)).ToArray();
        var duplicate = results.GroupBy(result => result.ScenarioId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Duplicate Midge benchmark scenario '{duplicate.Key}'.");
        }

        return results;
    }

    static MidgeBenchmarkResult ReadSummary(
        JsonElement summary,
        string sourceSha,
        string cpu,
        string operatingSystem,
        string runtime,
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
        var tier = Required(summary, "tier").GetUInt32();

        return new MidgeBenchmarkResult(
            $"midge:{id}" + (parameterText.Length == 0 ? string.Empty : $":{parameterText}"),
            $"Tier{tier.ToString(CultureInfo.InvariantCulture)}",
            RequiredString(summary, "name"),
            parameterText,
            RequiredString(summary, "primary_metric"),
            mean,
            RequiredString(summary, "quality"),
            RequiredString(summary, "trust_class"),
            sourceSha,
            cpu,
            operatingSystem,
            runtime,
            toolVersion);
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
