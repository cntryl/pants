using System.Security.Cryptography;
using System.Text.Json;

namespace Cntryl.Pants.Tests.Compatibility;

public sealed class MidgeQualificationRepositoryTests
{
    const string PinnedMidgeSha = "75dcc39f7a9b87df480ed91c3a5c93fe1389ca71";
    const string PinnedMidgeLockSha256 =
        "1fe29024e1789245b1ca8b20274aea17573380d5e33cf8f1811b59a65f85f937";

    [Fact]
    public void ShouldKeepDocumentedQualificationEntrypointsExecutableAndPinned()
    {
        var repository = FindRepositoryRoot();
        var harness = Path.Combine(
            repository,
            "eng",
            "compat",
            "Pants.CompatibilityHarness",
            "Pants.CompatibilityHarness.csproj");
        var driver = Path.Combine(repository, "eng", "compat", "MidgeDriver", "pants_compat.rs");
        var builder = Path.Combine(
            repository,
            "eng",
            "compat",
            "Pants.CompatibilityHarness",
            "Internal",
            "MidgeCheckoutBuilder.cs");
        var workflow = Path.Combine(
            repository,
            ".github",
            "workflows",
            "compatibility-qualification.yml");
        var documentation = File.ReadAllText(Path.Combine(
            repository,
            "docs",
            "compatibility",
            "MidgeQualification.md"));

        Assert.True(File.Exists(harness), $"Missing compatibility harness: {harness}");
        Assert.True(File.Exists(driver), $"Missing Midge compatibility driver: {driver}");
        Assert.True(File.Exists(workflow), $"Missing compatibility workflow: {workflow}");
        Assert.Contains("Pants.CompatibilityHarness.csproj", documentation, StringComparison.Ordinal);
        Assert.Contains(PinnedMidgeSha, documentation, StringComparison.Ordinal);
        var workflowText = File.ReadAllText(workflow);
        Assert.Contains($"ref: {PinnedMidgeSha}", workflowText, StringComparison.Ordinal);
        var builderText = File.ReadAllText(builder);
        Assert.Contains("\"--locked\"", builderText, StringComparison.Ordinal);
        Assert.Contains(PinnedMidgeLockSha256, builderText, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRecordCurrentMidgeRevisionAndDependencyLockInFixtureMetadata()
    {
        var metadataPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Compatibility",
            "fixture-metadata.json");
        using var metadata = JsonDocument.Parse(File.ReadAllBytes(metadataPath));
        var root = metadata.RootElement;

        Assert.Equal(PinnedMidgeSha, root.GetProperty("midgeSha").GetString());
        var driverLock = Assert.Single(
            root.GetProperty("artifacts").EnumerateArray(),
            static artifact =>
                artifact.GetProperty("id").GetString() == "midge-driver-cargo-lock");
        Assert.Equal(PinnedMidgeLockSha256, driverLock.GetProperty("sha256").GetString());
        var lockPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Compatibility",
            Assert.IsType<string>(driverLock.GetProperty("path").GetString()));
        Assert.Equal(
            PinnedMidgeLockSha256,
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(lockPath))));
    }

    static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Pants.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Pants repository root.");
    }
}
