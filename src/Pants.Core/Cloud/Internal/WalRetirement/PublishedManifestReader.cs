namespace Cntryl.Pants.Cloud.Internal.WalRetirement;

/// <summary>
///     Reads the manifest that recovery would use from the control store, not the local cache:
///     retirement must prove coverage against what is published. The decoded manifest is cached by
///     object identity, so an unchanged manifest costs two HEADs per turn rather than a download.
/// </summary>
sealed class PublishedManifestReader(ICloudObjectStore controlStore, Action ensureLeaseValid, ResourceBudget? budget = null)
{
    readonly ResourceBudget _budget = budget ?? new ResourceBudget(64L * 1024 * 1024);
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
        using var reads = new ProviderCloudControlMetadataReadSet(controlStore, _budget);
        var files = await CloudMetadataAuthority.ReadCommittedFilesAsync(controlStore, cancellationToken, readSet: reads).ConfigureAwait(false);
        ensureLeaseValid();
        if (files?.ContainsKey("manifest.snapshot.json") != true)
        {
            return null;
        }

        if (await ReadIdentityAsync(cancellationToken).ConfigureAwait(false) != identity)
        {
            throw new PantsBusyException("Committed cloud metadata changed while capturing WAL retirement coverage.");
        }

        _cached = new PublishedManifest(CloudManifestReader.DecodeMetadata(files), identity);
        return _cached;
    }

    public async ValueTask<bool> IsCurrentAsync(
        PublishedManifestIdentity identity,
        CancellationToken cancellationToken) =>
        await ReadIdentityAsync(cancellationToken).ConfigureAwait(false) == identity;

    async ValueTask<PublishedManifestIdentity> ReadIdentityAsync(CancellationToken cancellationToken)
    {
        ensureLeaseValid();
        using var reads = new ProviderCloudControlMetadataReadSet(controlStore, _budget);
        var pointer = await reads.ReadAsync(CloudMetadataAuthority.PointerKey, cancellationToken).ConfigureAwait(false);
        ensureLeaseValid();
        return new PublishedManifestIdentity(pointer?.Version, null, pointer is null ? 0 : CloudMetadataAuthority.ReadGenerationOrdinal(pointer.Data.Span));
    }
}
