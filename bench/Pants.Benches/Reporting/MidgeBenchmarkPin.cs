using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cntryl.Pants.Reporting;

sealed record MidgeBenchmarkPin(
    string Release,
    string SourceSha,
    string StressToolVersion,
    string CargoLockSha256,
    string ScenarioInventorySha256,
    IReadOnlySet<string> ExpectedScenarioIds)
{
    public static MidgeBenchmarkPin Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var release = RequiredString(root, "release");
        var sourceSha = RequiredString(root, "source_sha");
        var stressToolVersion = RequiredString(root, "stress_tool_version");
        var cargoLockSha256 = RequiredString(root, "cargo_lock_sha256");
        var scenarioInventoryFile = RequiredString(root, "scenario_inventory_file");
        var scenarioInventorySha256 = RequiredString(root, "scenario_inventory_sha256");

        ValidateHexDigest(sourceSha, 40, "source_sha");
        ValidateHexDigest(cargoLockSha256, 64, "cargo_lock_sha256");
        ValidateHexDigest(scenarioInventorySha256, 64, "scenario_inventory_sha256");
        if (scenarioInventoryFile.Contains('/') || scenarioInventoryFile.Contains('\\') ||
            !Path.GetFileName(scenarioInventoryFile).Equals(scenarioInventoryFile, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Midge scenario inventory must be a file beside the pin.");
        }

        var pinDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var inventoryBytes = File.ReadAllBytes(Path.Combine(pinDirectory, scenarioInventoryFile));
        var actualInventorySha256 = Hash(inventoryBytes);
        if (!actualInventorySha256.Equals(scenarioInventorySha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Midge scenario inventory SHA '{actualInventorySha256}' does not match the configured pin.");
        }

        var scenarioIds = Encoding.UTF8.GetString(inventoryBytes)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (scenarioIds.Length == 0 || scenarioIds.Distinct(StringComparer.Ordinal).Count() != scenarioIds.Length)
        {
            throw new InvalidDataException("The configured Midge scenario inventory is empty or contains duplicates.");
        }

        return new MidgeBenchmarkPin(
            release,
            sourceSha,
            stressToolVersion,
            cargoLockSha256,
            actualInventorySha256,
            scenarioIds.ToHashSet(StringComparer.Ordinal));
    }

    static string RequiredString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"Midge pin is missing required '{propertyName}'.");

    static void ValidateHexDigest(string value, int length, string name)
    {
        if (value.Length != length || value.Any(static character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException($"Midge pin '{name}' must be a {length}-character lowercase hexadecimal digest.");
        }
    }

    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
