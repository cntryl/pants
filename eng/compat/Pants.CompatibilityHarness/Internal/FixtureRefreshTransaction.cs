namespace Cntryl.Pants.CompatibilityHarness.Internal;

internal sealed record FixtureRefreshTransaction(
    int SchemaVersion,
    string State,
    string PreviousFixturesSha256,
    string PreviousManifestSha256,
    string NextFixturesSha256,
    string NextManifestSha256);
