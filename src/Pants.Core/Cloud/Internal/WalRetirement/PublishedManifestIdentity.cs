namespace Cntryl.Pants.Cloud.Internal.WalRetirement;

/// <summary>
///     The provider versions of the two published manifest objects (null when absent). Equal
///     identities mean the published manifest has not changed between observations.
/// </summary>
readonly record struct PublishedManifestIdentity(string? SnapshotVersion, string? ManifestVersion)
{
    public bool IsEmpty => SnapshotVersion is null && ManifestVersion is null;
}
