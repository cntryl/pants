namespace Pants.CompatibilityHarness.Internal;

internal sealed record CompatibilityFixtureMetadata(
    int SchemaVersion,
    string MidgeSha,
    IReadOnlyList<CompatibilityFixtureArtifact> Artifacts);
