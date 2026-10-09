namespace Cntryl.Pants.Cloud.Internal.WalRetirement;

/// <summary>
///     Reads the manifest that recovery would use from the control store, not the local cache:
///     retirement must prove coverage against what is published. The decoded manifest is cached by
///     object identity, so an unchanged manifest costs two HEADs per turn rather than a download.
/// </summary>
sealed class PublishedManifestReader(ICloudObjectStore controlStore, Action ensureLeaseValid)
{
    const string SnapshotObjectKey = PantsCloudObjectLayout.MetadataPrefix + "manifest.snapshot.json";
    const string ManifestObjectKey = PantsCloudObjectLayout.MetadataPrefix + "manifest.json";

    PublishedManifest? _cached;

    /// <returns>The published manifest, or null when none is published yet.</returns>
    public async ValueTask<PublishedManifest?> ReadAsync(CancellationToken cancellationToken)
    {
        var identity = await ReadIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (identity.IsEmpty)
        {
            return null;
        }

        if (_cached is { } cached && cached.Identity == identity)
        {
            return cached;
        }

        ensureLeaseValid();
        var snapshot = await controlStore.GetAsync(SnapshotObjectKey, cancellationToken)
            .ConfigureAwait(false);
        var manifest = await controlStore.GetAsync(ManifestObjectKey, cancellationToken)
            .ConfigureAwait(false);
        ensureLeaseValid();
        // The snapshot is authoritative when present, as it is for the local capture.
        if ((snapshot ?? manifest) is not { } authoritative)
        {
            return null;
        }

        _cached = new PublishedManifest(
            CloudManifestReader.DecodeManifest(authoritative.Data.Span),
            new PublishedManifestIdentity(snapshot?.Version, manifest?.Version));
        return _cached;
    }

    public async ValueTask<bool> IsCurrentAsync(
        PublishedManifestIdentity identity,
        CancellationToken cancellationToken) =>
        await ReadIdentityAsync(cancellationToken).ConfigureAwait(false) == identity;

    async ValueTask<PublishedManifestIdentity> ReadIdentityAsync(CancellationToken cancellationToken)
    {
        ensureLeaseValid();
        var snapshot = await controlStore.HeadAsync(SnapshotObjectKey, cancellationToken)
            .ConfigureAwait(false);
        var manifest = await controlStore.HeadAsync(ManifestObjectKey, cancellationToken)
            .ConfigureAwait(false);
        ensureLeaseValid();
        return new PublishedManifestIdentity(snapshot?.Version, manifest?.Version);
    }
}
