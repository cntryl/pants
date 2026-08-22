using System.Reflection;
using System.Text.Json;

namespace Pants.Tests;

public sealed class MidgeContractManifestTests
{
    private const string PinnedSha = "c5ffc2d3284c76b6f7cd03444a5b0a38ae8bbc33";
    private static readonly string[] ValidStatuses = ["mapped", "planned", "n/a"];

    [Fact]
    public void ShouldConsumeCommittedManifestWithoutSiblingCheckout()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "MidgeContractManifest.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;
        JsonElement entries = root.GetProperty("entries");

        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(PinnedSha, root.GetProperty("midgeSha").GetString());
        Assert.True(entries.GetArrayLength() > 900);
        Assert.All(entries.EnumerateArray(), static entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("source").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("sourceSymbolOrTest").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("observableBehavior").GetString()));
            string status = Assert.IsType<string>(entry.GetProperty("status").GetString());
            Assert.Contains(status, ValidStatuses);
            Assert.True(entry.TryGetProperty("issue", out JsonElement issue));
            Assert.True(issue.ValueKind is JsonValueKind.Number or JsonValueKind.Null);

            if (status == "mapped")
            {
                JsonElement pantsTests = entry.GetProperty("pantsTests");
                Assert.NotEmpty(pantsTests.EnumerateArray());
                Assert.All(pantsTests.EnumerateArray(), static pantsTest =>
                    Assert.True(TestExists(Assert.IsType<string>(pantsTest.GetString()))));
            }

            if (status == "n/a")
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("rationale").GetString()));
            }
        });
    }

    [Fact]
    public void ShouldMapEveryM1ContractToAnExecutablePantsTest()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "MidgeContractManifest.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));

        JsonElement[] plannedM1Contracts = document.RootElement
            .GetProperty("entries")
            .EnumerateArray()
            .Where(static entry =>
                entry.GetProperty("issue").ValueKind == JsonValueKind.Number &&
                entry.GetProperty("issue").GetInt32() is 1 or 2 or 3 &&
                entry.GetProperty("status").GetString() == "planned")
            .ToArray();

        Assert.Empty(plannedM1Contracts);
    }

    [Fact]
    public void ShouldMapEveryM15ContractToAnExecutablePantsTest()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "MidgeContractManifest.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));

        JsonElement[] plannedM15Contracts = document.RootElement
            .GetProperty("entries")
            .EnumerateArray()
            .Where(static entry =>
                entry.GetProperty("issue").ValueKind == JsonValueKind.Number &&
                entry.GetProperty("issue").GetInt32() is 5 or 15 or 16 or 17 &&
                entry.GetProperty("status").GetString() == "planned")
            .ToArray();

        Assert.Empty(plannedM15Contracts);
    }

    [Fact]
    public void ShouldMapEveryM2ContractToAnExecutablePantsTest()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "MidgeContractManifest.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));

        JsonElement[] plannedM2Contracts = document.RootElement
            .GetProperty("entries")
            .EnumerateArray()
            .Where(static entry =>
                entry.GetProperty("issue").ValueKind == JsonValueKind.Number &&
                entry.GetProperty("issue").GetInt32() is 4 or 6 &&
                entry.GetProperty("status").GetString() == "planned")
            .ToArray();

        Assert.Empty(plannedM2Contracts);
    }

    [Fact]
    public void ShouldMapEveryM3ContractToAnExecutablePantsTest()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "MidgeContractManifest.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));

        JsonElement[] plannedM3Contracts = document.RootElement
            .GetProperty("entries")
            .EnumerateArray()
            .Where(static entry =>
                entry.GetProperty("issue").ValueKind == JsonValueKind.Number &&
                entry.GetProperty("issue").GetInt32() is 7 or 8 &&
                entry.GetProperty("status").GetString() == "planned")
            .ToArray();

        Assert.Empty(plannedM3Contracts);
    }

    [Fact]
    public void ShouldReserveCompatibilityFixturesForM5Qualification()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "MidgeContractManifest.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var fixtures = document.RootElement
            .GetProperty("entries")
            .EnumerateArray()
            .Where(static entry =>
                entry.GetProperty("source").GetString() == "tests/compatibility_fixtures.rs")
            .ToArray();

        Assert.NotEmpty(fixtures);
        Assert.All(fixtures, static entry =>
            Assert.Equal(11, entry.GetProperty("issue").GetInt32()));
    }

    [Fact]
    public void ShouldMarkRetiredCliContractsAsNotApplicable()
    {
        string[] retiredCliContracts =
        [
            "should_emit_json_error_object_given_verify_failure_when_json_flag_requested",
            "should_exit_four_given_corrupt_database_when_midge_verify_runs",
            "should_exit_three_given_inaccessible_storage_when_midge_verify_runs",
            "should_exit_zero_given_healthy_database_when_midge_verify_runs",
            "should_report_usage_error_given_missing_db_path_when_verify_invoked",
            "should_treat_unrecognized_flag_as_path_given_typoed_json_flag_when_verify_invoked"
        ];
        var path = Path.Combine(AppContext.BaseDirectory, "MidgeContractManifest.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var entries = document.RootElement
            .GetProperty("entries")
            .EnumerateArray()
            .Where(static entry => entry.GetProperty("source").GetString() == "tests/observability_api.rs")
            .ToDictionary(
                static entry => Assert.IsType<string>(entry.GetProperty("sourceSymbolOrTest").GetString()),
                static entry => entry,
                StringComparer.Ordinal);

        Assert.All(retiredCliContracts, contract =>
        {
            var entry = Assert.Contains(contract, entries);
            Assert.Equal("n/a", entry.GetProperty("status").GetString());
            Assert.Contains(
                "CLI",
                Assert.IsType<string>(entry.GetProperty("rationale").GetString()),
                StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool TestExists(string fullyQualifiedName)
    {
        int separator = fullyQualifiedName.LastIndexOf('.');
        if (separator <= 0 || separator == fullyQualifiedName.Length - 1)
        {
            return false;
        }

        string typeName = fullyQualifiedName[..separator];
        string methodName = fullyQualifiedName[(separator + 1)..];
        Type? type = typeof(MidgeContractManifestTests).Assembly.GetType(typeName);
        return type?.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static) is not null;
    }
}
