using System.Security.Cryptography;
using System.Text.Json;

namespace Cntryl.Pants.Tests.Compatibility;

public sealed class CompatibilityFixtureMetadataTests
{
    const string PinnedMidgeSha = "c5ffc2d3284c76b6f7cd03444a5b0a38ae8bbc33";

    const string PinnedMidgeDriverLockSha256 =
        "e1740e05b3ff66b7744432f7346b8c585d8c437d9d1b69cb7e405fea836b046b";

    static readonly string[] RequiredStructures =
    [
        "format-v3",
        "wal-tlv",
        "wal-transaction-batch",
        "wal-frame",
        "wal-active-segment",
        "wal-sealed-epoch-segment",
        "sst-v4-raw-block",
        "sst-v4-lz4-block",
        "sst-v4-zstd3-block",
        "sst-v4-zstd9-block",
        "sst-compression-input",
        "sst-v4-index",
        "sst-v4-bloom",
        "sst-v4-trie",
        "sst-v4-range-tombstone",
        "sst-v4-footer",
        "manifest-snapshot",
        "manifest-journal",
        "intent-log",
        "local-lease",
        "cloud-lease",
        "ddl-registry",
        "ddl-prepare",
        "cloud-wal-catalog",
        "cloud-object-keys",
        "generated-database-tree",
        "midge-driver-dependency-lock"
    ];

    static readonly string[] ValidProducers = ["midge", "pants", "canonical"];
    static readonly string[] ValidCoverageKinds = ["exact", "semantic"];

    [Fact]
    public void ShouldCoverEveryPersistedStructureWithPinnedFixtureMetadata()
    {
        var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Compatibility");
        var metadataPath = Path.Combine(fixtureRoot, "fixture-metadata.json");
        using var metadata = JsonDocument.Parse(File.ReadAllBytes(metadataPath));
        var root = metadata.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(PinnedMidgeSha, root.GetProperty("midgeSha").GetString());
        var artifacts = root.GetProperty("artifacts").EnumerateArray().ToArray();
        Assert.NotEmpty(artifacts);

        var structures = artifacts
            .Select(static artifact => Assert.IsType<string>(artifact.GetProperty("structure").GetString()))
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(RequiredStructures, structure => Assert.Contains(structure, structures));

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            var identifier = Assert.IsType<string>(artifact.GetProperty("id").GetString());
            Assert.True(identifiers.Add(identifier), $"Duplicate fixture artifact id '{identifier}'.");
            Assert.Contains(
                artifact.GetProperty("producer").GetString(),
                ValidProducers);
            var coverage = Assert.IsType<string>(artifact.GetProperty("coverage").GetString());
            Assert.Contains(coverage, ValidCoverageKinds);
            if (coverage == "semantic")
            {
                Assert.False(string.IsNullOrWhiteSpace(artifact.GetProperty("rationale").GetString()));
            }

            var relativePath = Assert.IsType<string>(artifact.GetProperty("path").GetString());
            var artifactPath = ResolveContainedPath(fixtureRoot, relativePath);
            Assert.True(File.Exists(artifactPath), $"Missing fixture artifact '{relativePath}'.");
            var expectedHash = Assert.IsType<string>(artifact.GetProperty("sha256").GetString());
            var actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(artifactPath)))
                .ToLowerInvariant();
            Assert.Equal(expectedHash, actualHash);
        }

        var driverLock = Assert.Single(
            artifacts,
            static artifact =>
                artifact.GetProperty("id").GetString() == "midge-driver-cargo-lock");
        Assert.Equal(
            PinnedMidgeDriverLockSha256,
            driverLock.GetProperty("sha256").GetString());
    }

    static string ResolveContainedPath(string fixtureRoot, string relativePath)
    {
        Assert.False(Path.IsPathRooted(relativePath));
        var normalizedRoot = Path.GetFullPath(fixtureRoot) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fixtureRoot, relativePath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        Assert.StartsWith(normalizedRoot, fullPath, comparison);
        return fullPath;
    }
}
