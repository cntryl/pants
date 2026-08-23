using System.Reflection;
using System.Text.Json;

namespace Cntryl.Pants.Tests.Compatibility;

public sealed class MidgeContractManifestTests
{
    const string PinnedSha = "c5ffc2d3284c76b6f7cd03444a5b0a38ae8bbc33";
    static readonly string[] ValidStatuses = ["mapped", "planned", "n/a"];

    [Fact]
    public void ShouldConsumeCommittedManifestWithoutSiblingCheckout()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "MidgeContractManifest.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var entries = root.GetProperty("entries");

        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(PinnedSha, root.GetProperty("midgeSha").GetString());
        var sourceTreeSha = Assert.IsType<string>(
            root.GetProperty("sourceTreeSha256").GetString());
        Assert.Equal(64, sourceTreeSha.Length);
        Assert.All(sourceTreeSha, static value => Assert.True(char.IsAsciiHexDigitLower(value)));
        Assert.True(entries.GetArrayLength() > 900);
        Assert.All(entries.EnumerateArray(), static entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("source").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("sourceSymbolOrTest").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("observableBehavior").GetString()));
            var status = Assert.IsType<string>(entry.GetProperty("status").GetString());
            Assert.Contains(status, ValidStatuses);
            Assert.True(entry.TryGetProperty("issue", out var issue));
            Assert.True(issue.ValueKind is JsonValueKind.Number or JsonValueKind.Null);

            if (status == "mapped")
            {
                var pantsTests = entry.GetProperty("pantsTests");
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
        var path = Path.Combine(AppContext.BaseDirectory, "MidgeContractManifest.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));

        var plannedM1Contracts = document.RootElement
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
        var path = Path.Combine(AppContext.BaseDirectory, "MidgeContractManifest.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));

        var plannedM15Contracts = document.RootElement
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
        var path = Path.Combine(AppContext.BaseDirectory, "MidgeContractManifest.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));

        var plannedM2Contracts = document.RootElement
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
        var path = Path.Combine(AppContext.BaseDirectory, "MidgeContractManifest.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));

        var plannedM3Contracts = document.RootElement
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
    public void ShouldMapEveryM4ContractToAnExecutablePantsTest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "MidgeContractManifest.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));

        var plannedM4Contracts = document.RootElement
            .GetProperty("entries")
            .EnumerateArray()
            .Where(static entry =>
                entry.GetProperty("issue").ValueKind == JsonValueKind.Number &&
                entry.GetProperty("issue").GetInt32() is 9 or 10 &&
                entry.GetProperty("status").GetString() == "planned")
            .ToArray();

        Assert.Empty(plannedM4Contracts);
    }

    [Fact]
    public void ShouldMapEveryM5ContractToAnExecutablePantsTest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "MidgeContractManifest.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));

        var plannedM5Contracts = document.RootElement
            .GetProperty("entries")
            .EnumerateArray()
            .Where(static entry =>
                entry.GetProperty("issue").ValueKind == JsonValueKind.Number &&
                entry.GetProperty("issue").GetInt32() == 11 &&
                entry.GetProperty("status").GetString() == "planned")
            .ToArray();

        Assert.Empty(plannedM5Contracts);
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

    [Fact]
    public void ShouldMarkRustArchitectureContractsAsNotApplicable()
    {
        var entries = ReadEntries("tests/architecture_ladder.rs");

        Assert.Equal(7, entries.Length);
        Assert.All(entries, static entry =>
        {
            Assert.Equal("n/a", entry.GetProperty("status").GetString());
            Assert.Contains(
                "private implementation",
                Assert.IsType<string>(entry.GetProperty("rationale").GetString()),
                StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void ShouldMarkMidgeRepositoryToolingContractsAsNotApplicable()
    {
        var entries = ReadEntries("tests/testing_governance.rs");

        Assert.Equal(10, entries.Length);
        Assert.All(entries, static entry =>
        {
            Assert.Equal("n/a", entry.GetProperty("status").GetString());
            Assert.Contains(
                "repository tooling",
                Assert.IsType<string>(entry.GetProperty("rationale").GetString()),
                StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void ShouldMapEveryExternalAdopterSmokeContractToExecutablePantsTest()
    {
        var entries = ReadEntries("tests/external_adopter_smoke.rs");

        Assert.Equal(4, entries.Length);
        Assert.All(entries, static entry =>
        {
            Assert.Equal("mapped", entry.GetProperty("status").GetString());
            var pantsTests = entry.GetProperty("pantsTests").EnumerateArray().ToArray();
            Assert.NotEmpty(pantsTests);
            Assert.All(pantsTests, static pantsTest =>
                Assert.True(TestExists(Assert.IsType<string>(pantsTest.GetString()))));
        });
    }

    static JsonElement[] ReadEntries(string source)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "MidgeContractManifest.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement
            .GetProperty("entries")
            .EnumerateArray()
            .Where(entry => entry.GetProperty("source").GetString() == source)
            .Select(static entry => entry.Clone())
            .ToArray();
    }

    static bool TestExists(string fullyQualifiedName)
    {
        var separator = fullyQualifiedName.LastIndexOf('.');
        if (separator <= 0 || separator == fullyQualifiedName.Length - 1)
        {
            return false;
        }

        var typeName = fullyQualifiedName[..separator];
        var methodName = fullyQualifiedName[(separator + 1)..];
        var type = typeof(MidgeContractManifestTests).Assembly.GetType(typeName);
        return type?.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static) is not null;
    }
}
