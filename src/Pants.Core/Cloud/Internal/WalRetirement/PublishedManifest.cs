namespace Cntryl.Pants.Cloud.Internal.WalRetirement;

/// <summary>The authoritative published manifest, its identity, and its coverage index.</summary>
sealed class PublishedManifest(ManifestState manifest, PublishedManifestIdentity identity)
{
    public ManifestState Manifest { get; } = manifest;

    public PublishedManifestIdentity Identity { get; } = identity;

    public WalCoverageIndex Coverage { get; } = new(manifest);

    public ulong LastPersistedSequence => Manifest.LastPersistedSequence;
}
