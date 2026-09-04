using System.Security.Cryptography;
using System.Text.Json;
using Cntryl.Pants.CompatibilityHarness.Internal;

namespace Cntryl.Pants.CompatibilityHarness.Tests;

public sealed class CompatibilityBaselineComparerTests
{
    static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void ShouldAcceptRegeneratedSemanticBytesGivenDefinitionsMatch()
    {
        using var directory = new CompatibilityTestDirectory();
        var repository = directory.CreateRepository("old-semantic-value", "manifest");
        using var workspace = CreateWorkspace(repository, "new-semantic-value", "manifest");
        WriteMetadata(repository.CompatibilityFixtures, "semantic");
        WriteMetadata(workspace.CompatibilityFixtures, "semantic");

        CompatibilityBaselineComparer.EnsureEquivalent(repository, workspace);
    }

    [Fact]
    public void ShouldRejectRegeneratedExactBytesGivenCommittedBaselineIsStale()
    {
        using var directory = new CompatibilityTestDirectory();
        var repository = directory.CreateRepository("old-exact-value", "manifest");
        using var workspace = CreateWorkspace(repository, "new-exact-value", "manifest");
        WriteMetadata(repository.CompatibilityFixtures, "exact");
        WriteMetadata(workspace.CompatibilityFixtures, "exact");

        var exception = Assert.Throws<InvalidOperationException>(
            () => CompatibilityBaselineComparer.EnsureEquivalent(repository, workspace));

        Assert.Contains("exact fixture artifact", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRejectUntrackedDeterministicArtifactGivenMetadataMatches()
    {
        using var directory = new CompatibilityTestDirectory();
        var repository = directory.CreateRepository("exact-value", "manifest");
        using var workspace = CreateWorkspace(repository, "exact-value", "manifest");
        WriteMetadata(repository.CompatibilityFixtures, "exact");
        WriteMetadata(workspace.CompatibilityFixtures, "exact");
        File.WriteAllText(
            Path.Combine(workspace.CompatibilityFixtures, "untracked.bin"),
            "new-artifact");

        var exception = Assert.Throws<InvalidOperationException>(
            () => CompatibilityBaselineComparer.EnsureEquivalent(repository, workspace));

        Assert.Contains("file set changed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRejectMissingSemanticArtifactGivenMetadataStillDeclaresIt()
    {
        using var directory = new CompatibilityTestDirectory();
        var repository = directory.CreateRepository("semantic-value", "manifest");
        using var workspace = CreateWorkspace(repository, "semantic-value", "manifest");
        WriteMetadata(repository.CompatibilityFixtures, "semantic");
        WriteMetadata(workspace.CompatibilityFixtures, "semantic");
        File.Delete(Path.Combine(repository.CompatibilityFixtures, "fixture.txt"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => CompatibilityBaselineComparer.EnsureEquivalent(repository, workspace));

        Assert.Contains("file set changed", exception.Message, StringComparison.Ordinal);
    }

    static FixtureRefreshWorkspace CreateWorkspace(
        PantsRepositoryPaths repository,
        string fixtureValue,
        string manifestValue)
    {
        var workspace = new FixtureRefreshWorkspace(repository);
        File.WriteAllText(
            Path.Combine(workspace.CompatibilityFixtures, "fixture.txt"),
            fixtureValue);
        File.WriteAllText(workspace.ContractManifest, manifestValue);
        return workspace;
    }

    static void WriteMetadata(string fixtureRoot, string coverage)
    {
        var fixturePath = Path.Combine(fixtureRoot, "fixture.txt");
        var artifact = new CompatibilityFixtureArtifact(
            "fixture",
            "fixture-structure",
            "midge",
            coverage,
            "fixture.txt",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixturePath))),
            coverage == "semantic" ? "Runtime-generated value." : null);
        var metadata = new CompatibilityFixtureMetadata(1, "midge-sha", [artifact]);
        File.WriteAllText(
            Path.Combine(fixtureRoot, "fixture-metadata.json"),
            JsonSerializer.Serialize(metadata, MetadataJsonOptions));
    }
}
