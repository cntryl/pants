using System.Security.Cryptography;
using System.Text.Json;

namespace Cntryl.Pants.CompatibilityHarness.Internal;

internal static class CompatibilityBaselineComparer
{
    static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void EnsureEquivalent(
        PantsRepositoryPaths repository,
        FixtureRefreshWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(workspace);

        EnsureFileEqual(
            repository.ContractManifest,
            workspace.ContractManifest,
            "contract manifest");

        var current = ReadMetadata(repository.CompatibilityFixtures);
        var refreshed = ReadMetadata(workspace.CompatibilityFixtures);
        EnsureMetadataEquivalent(current, refreshed);

        var semanticPaths = current.Artifacts
            .Concat(refreshed.Artifacts)
            .Where(static artifact => artifact.Coverage == "semantic")
            .Select(static artifact => artifact.Path)
            .ToHashSet(StringComparer.Ordinal);
        EnsureFixtureTreesEqual(
            repository.CompatibilityFixtures,
            workspace.CompatibilityFixtures,
            semanticPaths);
    }

    static CompatibilityFixtureMetadata ReadMetadata(string fixtureRoot)
    {
        var path = Path.Combine(fixtureRoot, "fixture-metadata.json");
        var metadata = JsonSerializer.Deserialize<CompatibilityFixtureMetadata>(
                File.ReadAllBytes(path),
                MetadataJsonOptions)
            ?? throw new InvalidDataException(
                $"Compatibility fixture metadata '{path}' is empty.");
        if (metadata.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Compatibility fixture metadata schema '{metadata.SchemaVersion}' is unsupported.");
        }

        return metadata;
    }

    static void EnsureMetadataEquivalent(
        CompatibilityFixtureMetadata current,
        CompatibilityFixtureMetadata refreshed)
    {
        if (!StringComparer.Ordinal.Equals(current.MidgeSha, refreshed.MidgeSha))
        {
            throw StaleBaseline(
                $"fixture metadata Midge SHA changed from '{current.MidgeSha}' to "
                + $"'{refreshed.MidgeSha}'");
        }

        var currentArtifacts = current.Artifacts.ToDictionary(
            static artifact => artifact.Id,
            StringComparer.Ordinal);
        var refreshedArtifacts = refreshed.Artifacts.ToDictionary(
            static artifact => artifact.Id,
            StringComparer.Ordinal);
        if (!currentArtifacts.Keys.Order(StringComparer.Ordinal).SequenceEqual(
                refreshedArtifacts.Keys.Order(StringComparer.Ordinal),
                StringComparer.Ordinal))
        {
            throw StaleBaseline("fixture metadata artifact identifiers changed");
        }

        foreach (var (identifier, currentArtifact) in currentArtifacts)
        {
            var refreshedArtifact = refreshedArtifacts[identifier];
            var definitionsMatch = StringComparer.Ordinal.Equals(
                    currentArtifact.Structure,
                    refreshedArtifact.Structure)
                && StringComparer.Ordinal.Equals(
                    currentArtifact.Producer,
                    refreshedArtifact.Producer)
                && StringComparer.Ordinal.Equals(
                    currentArtifact.Coverage,
                    refreshedArtifact.Coverage)
                && StringComparer.Ordinal.Equals(currentArtifact.Path, refreshedArtifact.Path)
                && StringComparer.Ordinal.Equals(
                    currentArtifact.Rationale,
                    refreshedArtifact.Rationale);
            if (!definitionsMatch)
            {
                throw StaleBaseline(
                    $"fixture metadata definition changed for artifact '{identifier}'");
            }

            if (currentArtifact.Coverage == "exact"
                && !StringComparer.Ordinal.Equals(
                    currentArtifact.Sha256,
                    refreshedArtifact.Sha256))
            {
                throw StaleBaseline(
                    $"exact fixture artifact '{identifier}' changed from "
                    + $"'{currentArtifact.Sha256}' to '{refreshedArtifact.Sha256}'");
            }
        }
    }

    static void EnsureFixtureTreesEqual(
        string currentRoot,
        string refreshedRoot,
        HashSet<string> semanticPaths)
    {
        var currentFiles = EnumerateFiles(currentRoot);
        var refreshedFiles = EnumerateFiles(refreshedRoot);
        if (!currentFiles.Keys.SequenceEqual(refreshedFiles.Keys, StringComparer.Ordinal))
        {
            throw StaleBaseline("the deterministic fixture file set changed");
        }

        foreach (var relativePath in currentFiles.Keys)
        {
            if (semanticPaths.Contains(relativePath))
            {
                continue;
            }

            EnsureFileEqual(
                currentFiles[relativePath],
                refreshedFiles[relativePath],
                $"fixture '{relativePath}'");
        }
    }

    static SortedDictionary<string, string> EnumerateFiles(string root)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (relativePath != "fixture-metadata.json")
            {
                result.Add(relativePath, path);
            }
        }

        return result;
    }

    static void EnsureFileEqual(string currentPath, string refreshedPath, string description)
    {
        var current = SHA256.HashData(File.ReadAllBytes(currentPath));
        var refreshed = SHA256.HashData(File.ReadAllBytes(refreshedPath));
        if (!current.AsSpan().SequenceEqual(refreshed))
        {
            throw StaleBaseline($"{description} changed");
        }
    }

    static InvalidOperationException StaleBaseline(string reason) =>
        new(
            $"The committed compatibility baseline is stale: {reason}. Run the refresh "
            + "command, review the generated changes, and commit them together.");
}
