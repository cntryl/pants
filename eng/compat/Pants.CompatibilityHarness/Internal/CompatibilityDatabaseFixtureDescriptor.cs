namespace Cntryl.Pants.CompatibilityHarness.Internal;

internal sealed record CompatibilityDatabaseFixtureDescriptor(
    int SchemaVersion,
    string MidgeSha,
    string Path,
    string Sha256);
