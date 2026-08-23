using System.Text.Json;

namespace Cntryl.Pants.CompatibilityHarness.Internal;

internal static class CompatibilityDatabaseFixtureDescriptorWriter
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void Write(string fixtureRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureRoot);

        var sha = MidgeCheckoutBuilder.RequiredCommit;
        const string databaseName = "midge-structured-v4-db";
        var databaseDirectory = Path.Combine(
            fixtureRoot,
            "Storage",
            sha,
            "databases");
        var databasePath = Path.Combine(databaseDirectory, databaseName);
        var descriptor = new CompatibilityDatabaseFixtureDescriptor(
            1,
            sha,
            databaseName,
            DirectoryTreeFingerprint.Compute(databasePath));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(descriptor, JsonOptions);
        File.WriteAllBytes(
            Path.Combine(databaseDirectory, $"{databaseName}.fixture.json"),
            [.. bytes, (byte)'\n']);
    }
}
