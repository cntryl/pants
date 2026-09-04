using System.Security.Cryptography;
using System.Text.Json;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeQualificationRepositoryTests
{
    const string PinnedMidgeSha = "75dcc39f7a9b87df480ed91c3a5c93fe1389ca71";

    const string PinnedMidgeLockSha256 =
        "1fe29024e1789245b1ca8b20274aea17573380d5e33cf8f1811b59a65f85f937";

    [Fact]
    public void ShouldKeepCommittedCompatibilityBaselineDocumentedAndPinned()
    {
        var repository = FindRepositoryRoot();
        var manifest = Path.Combine(
            repository,
            "test",
            "Pants.Tests",
            "MidgeContractManifest.json");
        var documentation = File.ReadAllText(Path.Combine(
            repository,
            "docs",
            "compatibility",
            "MidgeQualification.md"));

        Assert.True(File.Exists(manifest), $"Missing compatibility manifest: {manifest}");
        Assert.Contains(PinnedMidgeSha, documentation, StringComparison.Ordinal);
        Assert.DoesNotContain("Pants.CompatibilityHarness", documentation, StringComparison.Ordinal);
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
