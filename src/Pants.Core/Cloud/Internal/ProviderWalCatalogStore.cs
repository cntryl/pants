using Cntryl.Pants.Cloud.Internal.Objects;

namespace Cntryl.Pants.Cloud.Internal;

/// <summary>
///     Reads and converges the cloud WAL publication catalog's two copies: the primary and a
///     byte-identical mirror, so one torn or lost copy cannot make acknowledged cloud-only writes
///     unrecoverable.
/// </summary>
/// <remarks>
///     A valid primary is authoritative and the mirror is converged to its bytes. A torn or missing
///     primary is repaired from a valid mirror by compare-and-swap. Both copies invalid, or a
///     missing primary beside an invalid mirror, fails closed without mutating either object. With
///     neither copy present, any existing WAL segment object means the catalog was lost, which also
///     fails closed rather than starting a fresh catalog over unreferenced segments.
/// </remarks>
sealed class ProviderWalCatalogStore(
    ICloudObjectStore walStore,
    Func<ReadOnlyMemory<byte>, ProviderWalCatalog> decode,
    Action ensureLeaseValid,
    ProviderWalCatalogReadAdmission? readAdmission = null)
{
    const int MaximumRepairAttempts = 4;

    readonly ProviderWalCatalogReadAdmission _readAdmission =
        readAdmission ?? ProviderWalCatalogReadAdmission.Standalone();

    /// <param name="rejectOrphanedWalSegments">
    ///     At startup recovery, fail closed when neither catalog copy exists but WAL segment objects
    ///     do. A writer publishing its first segments uploads them before the catalog exists, so
    ///     publication must not ask for this.
    /// </param>
    /// <param name="waitForMaintenanceGate">
    ///     Background maintenance and startup wait their turn at the shared memory gate. Foreground
    ///     WAL publication reserves from the same pool without waiting behind a provider read that
    ///     may be stalled; it fails before reading if that reservation cannot be made.
    /// </param>
    public async ValueTask<WalCatalogRead> ReadAsync(
        bool rejectOrphanedWalSegments,
        CancellationToken cancellationToken,
        bool waitForMaintenanceGate = true)
    {
        for (var attempt = 0; attempt < MaximumRepairAttempts; attempt++)
        {
            ensureLeaseValid();
            var primaryMetadata = await walStore.HeadAsync(
                PantsCloudObjectLayout.WalCatalogObjectKey,
                cancellationToken).ConfigureAwait(false);
            var mirrorMetadata = await walStore.HeadAsync(
                PantsCloudObjectLayout.WalCatalogMirrorObjectKey,
                cancellationToken).ConfigureAwait(false);
            var readLease = await _readAdmission.AdmitAsync(
                primaryMetadata,
                mirrorMetadata,
                cancellationToken,
                waitForMaintenanceGate).ConfigureAwait(false);
            try
            {
                var primary = await ReadObjectAsync(
                    PantsCloudObjectLayout.WalCatalogObjectKey,
                    primaryMetadata,
                    cancellationToken).ConfigureAwait(false);
                var mirror = await ReadObjectAsync(
                    PantsCloudObjectLayout.WalCatalogMirrorObjectKey,
                    mirrorMetadata,
                    cancellationToken).ConfigureAwait(false);
                var primaryCatalog = TryDecode(primary, out var primaryError);
                var mirrorCatalog = TryDecode(mirror, out var mirrorError);

                if (primary is not null && primaryCatalog is not null)
                {
                    if (mirror is null || !mirror.Data.Span.SequenceEqual(primary.Data.Span))
                    {
                        await ConvergeMirrorAsync(
                                primary.Data,
                                primary.Version,
                                mirrorMetadata,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    return TransferRead(primary.Version, primaryCatalog, ref readLease);
                }

                if (mirror is null || mirrorCatalog is null)
                {
                    if (primary is null && mirror is null)
                    {
                        if (rejectOrphanedWalSegments)
                        {
                            await RejectOrphanedWalSegmentsAsync(cancellationToken).ConfigureAwait(false);
                        }

                        return TransferRead(null, null, ref readLease);
                    }

                    throw primaryError ?? mirrorError ??
                          new PantsCorruptionException(
                              "Both cloud WAL publication catalog copies are unusable.");
                }

                // The mirror is valid and the primary is torn or missing: repair the primary.
                var repaired = await walStore.PutAsync(
                    PantsCloudObjectLayout.WalCatalogObjectKey,
                    mirror.Data,
                    primary is null
                        ? new PantsCloudObjectWriteCondition.IfAbsent()
                        : new PantsCloudObjectWriteCondition.IfVersion(primary.Version),
                    cancellationToken).ConfigureAwait(false);
                if (repaired)
                {
                    var readbackMetadata = await walStore.HeadAsync(
                            PantsCloudObjectLayout.WalCatalogObjectKey,
                            cancellationToken)
                        .ConfigureAwait(false) ?? throw new PantsLeaseIndeterminateException(
                        "The cloud WAL catalog repair was acknowledged without an authoritative object.");
                    if (readbackMetadata.SizeBytes != checked((ulong)mirror.Data.Length))
                    {
                        throw new PantsCorruptionException(
                            "The cloud WAL catalog repair read back a different byte length after CAS.");
                    }

                    var readback = await ReadObjectAsync(
                            PantsCloudObjectLayout.WalCatalogObjectKey,
                            readbackMetadata,
                            cancellationToken)
                        .ConfigureAwait(false) ?? throw new PantsLeaseIndeterminateException(
                        "The cloud WAL catalog repair was acknowledged without an authoritative object.");
                    if (!readback.Data.Span.SequenceEqual(mirror.Data.Span))
                    {
                        throw new PantsCorruptionException(
                            "The cloud WAL catalog repair read back different bytes after CAS.");
                    }

                    return TransferRead(readback.Version, decode(readback.Data), ref readLease);
                }
            }
            finally
            {
                readLease?.Dispose();
            }
        }

        throw new PantsBusyException("Cloud WAL catalog repair exceeded its bounded CAS retries.");
    }

    /// <summary>The catalog's fencing epoch, or 0 when no catalog exists yet.</summary>
    public async ValueTask<ulong> ReadFencingEpochAsync(CancellationToken cancellationToken)
    {
        using var read = await ReadAsync(false, cancellationToken).ConfigureAwait(false);
        return read.Catalog is null
            ? 0
            : Math.Max(
                read.Catalog.FencingEpoch,
                read.Catalog.Segments.Values.Select(static segment => segment.WriterEpoch)
                    .DefaultIfEmpty()
                    .Max());
    }

    static WalCatalogRead TransferRead(
        string? version,
        ProviderWalCatalog? catalog,
        ref ProviderWalCatalogReadLease? readLease)
    {
        readLease?.ReleaseGate();
        var read = new WalCatalogRead(version, catalog, readLease);
        readLease = null;
        return read;
    }

    async ValueTask<CloudObject?> ReadObjectAsync(
        string objectKey,
        CloudObjectMetadata? metadata,
        CancellationToken cancellationToken)
    {
        if (metadata is null)
        {
            return null;
        }

        if (metadata.SizeBytes > int.MaxValue)
        {
            throw new PantsResourceLimitException(
                $"Cloud WAL catalog object '{objectKey}' exceeds the supported byte-array size.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)metadata.SizeBytes));
        for (var offset = 0; offset < bytes.Length;)
        {
            var length = Math.Min(ProviderWalCatalogReadAdmission.ReadRangeBytes, bytes.Length - offset);
            CloudObject? range;
            try
            {
                range = await walStore.GetRangeAsync(
                    objectKey,
                    checked((ulong)offset),
                    length,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (PantsNotSupportedException) when (
                metadata.SizeBytes <= ProviderWalCatalogReadAdmission.ReadRangeBytes)
            {
                return await ReadSmallWholeObjectAsync(
                        objectKey,
                        metadata,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PantsNotSupportedException exception)
            {
                throw new PantsResourceLimitException(
                    $"Cloud WAL catalog object '{objectKey}' is too large for a store without " +
                    "bounded ranged reads.",
                    exception);
            }

            if (range is null)
            {
                throw new PantsBusyException(
                    $"Cloud WAL catalog object '{objectKey}' changed during a bounded read.");
            }

            if (!StringComparer.Ordinal.Equals(range.Version, metadata.Version))
            {
                throw new PantsBusyException(
                    $"Cloud WAL catalog object '{objectKey}' changed during a bounded read.");
            }

            if (range.Data.Length != length)
            {
                throw new PantsCorruptionException(
                    $"Cloud WAL catalog object '{objectKey}' returned an incomplete range.");
            }

            range.Data.Span.CopyTo(bytes.AsSpan(offset, length));
            offset += length;
        }

        return new CloudObject(bytes, metadata.Version);
    }

    async ValueTask<CloudObject> ReadSmallWholeObjectAsync(
        string objectKey,
        CloudObjectMetadata metadata,
        CancellationToken cancellationToken)
    {
        var whole = await walStore.GetAsync(objectKey, cancellationToken).ConfigureAwait(false) ??
                    throw new PantsBusyException(
                        $"Cloud WAL catalog object '{objectKey}' changed during a bounded read.");
        if (!StringComparer.Ordinal.Equals(whole.Version, metadata.Version))
        {
            throw new PantsBusyException(
                $"Cloud WAL catalog object '{objectKey}' changed during a bounded read.");
        }

        if (whole.Data.Length != checked((int)metadata.SizeBytes))
        {
            throw new PantsCorruptionException(
                $"Cloud WAL catalog object '{objectKey}' returned a different byte length than HEAD.");
        }

        return whole;
    }

    /// <summary>
    ///     Converges the mirror to <paramref name="primaryBytes" /> after the primary has been
    ///     committed and read back. A lost primary CAS never reaches here, so a losing writer cannot
    ///     publish a mirror.
    /// </summary>
    public async ValueTask ConvergeMirrorAsync(
        ReadOnlyMemory<byte> primaryBytes,
        string primaryVersion,
        CancellationToken cancellationToken)
    {
        var mirror = await walStore.HeadAsync(
            PantsCloudObjectLayout.WalCatalogMirrorObjectKey,
            cancellationToken).ConfigureAwait(false);
        await ConvergeMirrorAsync(primaryBytes, primaryVersion, mirror, cancellationToken)
            .ConfigureAwait(false);
    }

    async ValueTask ConvergeMirrorAsync(
        ReadOnlyMemory<byte> primaryBytes,
        string primaryVersion,
        CloudObjectMetadata? mirror,
        CancellationToken cancellationToken)
    {
        ensureLeaseValid();
        for (var attempt = 0; attempt < MaximumRepairAttempts; attempt++)
        {
            var written = await walStore.PutAsync(
                PantsCloudObjectLayout.WalCatalogMirrorObjectKey,
                primaryBytes,
                mirror is null
                    ? new PantsCloudObjectWriteCondition.IfAbsent()
                    : new PantsCloudObjectWriteCondition.IfVersion(mirror.Version),
                cancellationToken).ConfigureAwait(false);
            if (written)
            {
                ensureLeaseValid();
                return;
            }

            // Another writer moved the mirror. If the primary has moved on too, the committer
            // that moved it owns convergence; writing these now-stale bytes would regress the mirror.
            var primary = await walStore.HeadAsync(
                PantsCloudObjectLayout.WalCatalogObjectKey,
                cancellationToken).ConfigureAwait(false);
            if (primary is null || !StringComparer.Ordinal.Equals(primary.Version, primaryVersion))
            {
                return;
            }

            mirror = await walStore.HeadAsync(
                PantsCloudObjectLayout.WalCatalogMirrorObjectKey,
                cancellationToken).ConfigureAwait(false);
        }

        throw new PantsBusyException("Cloud WAL catalog mirror convergence exceeded its bounded CAS retries.");
    }

    async ValueTask RejectOrphanedWalSegmentsAsync(CancellationToken cancellationToken)
    {
        var keys = await walStore.ListAllAsync(
            PantsCloudObjectLayout.WalPrefix,
            cancellationToken).ConfigureAwait(false);
        if (keys.Any(IsWalSegmentKey))
        {
            throw new PantsRecoveryFailedException(
                $"Cloud WAL segment objects exist but the publication catalog " +
                $"'{PantsCloudObjectLayout.WalCatalogObjectKey}' and its mirror are both missing; " +
                "refusing to open and orphan acknowledged writes.");
        }
    }

    static bool IsWalSegmentKey(string key) =>
        key.StartsWith(PantsCloudObjectLayout.WalPrefix, StringComparison.Ordinal) &&
        key.EndsWith(".wal", StringComparison.Ordinal);

    ProviderWalCatalog? TryDecode(CloudObject? candidate, out PantsCorruptionException? error)
    {
        error = null;
        if (candidate is null)
        {
            return null;
        }

        try
        {
            return decode(candidate.Data);
        }
        catch (PantsCorruptionException exception)
        {
            error = exception;
            return null;
        }
    }
}

sealed class WalCatalogRead(
    string? version,
    ProviderWalCatalog? catalog,
    IDisposable? reservation) : IDisposable
{
    IDisposable? _reservation = reservation;

    public string? Version { get; } = version;

    public ProviderWalCatalog? Catalog { get; } = catalog;

    public void Dispose() => Interlocked.Exchange(ref _reservation, null)?.Dispose();
}
