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
    Action ensureLeaseValid)
{
    const int MaximumRepairAttempts = 4;

    /// <param name="rejectOrphanedWalSegments">
    ///     At startup recovery, fail closed when neither catalog copy exists but WAL segment objects
    ///     do. A writer publishing its first segments uploads them before the catalog exists, so
    ///     publication must not ask for this.
    /// </param>
    public async ValueTask<WalCatalogRead> ReadAsync(
        bool rejectOrphanedWalSegments,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumRepairAttempts; attempt++)
        {
            ensureLeaseValid();
            var primary = await walStore.GetAsync(
                PantsCloudObjectLayout.WalCatalogObjectKey,
                cancellationToken).ConfigureAwait(false);
            var mirror = await walStore.GetAsync(
                PantsCloudObjectLayout.WalCatalogMirrorObjectKey,
                cancellationToken).ConfigureAwait(false);
            var primaryCatalog = TryDecode(primary, out var primaryError);
            var mirrorCatalog = TryDecode(mirror, out var mirrorError);

            if (primary is not null && primaryCatalog is not null)
            {
                if (mirror is null || !mirror.Data.Span.SequenceEqual(primary.Data.Span))
                {
                    await ConvergeMirrorAsync(primary.Data, mirror, cancellationToken)
                        .ConfigureAwait(false);
                }

                return new WalCatalogRead(primary, primaryCatalog);
            }

            if (mirror is null || mirrorCatalog is null)
            {
                if (primary is null && mirror is null)
                {
                    if (rejectOrphanedWalSegments)
                    {
                        await RejectOrphanedWalSegmentsAsync(cancellationToken).ConfigureAwait(false);
                    }

                    return new WalCatalogRead(null, null);
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
                var readback = await walStore.GetAsync(
                                   PantsCloudObjectLayout.WalCatalogObjectKey,
                                   cancellationToken).ConfigureAwait(false) ??
                               throw new PantsLeaseIndeterminateException(
                                   "The cloud WAL catalog repair was acknowledged without an authoritative object.");
                if (!readback.Data.Span.SequenceEqual(mirror.Data.Span))
                {
                    throw new PantsCorruptionException(
                        "The cloud WAL catalog repair read back different bytes after CAS.");
                }

                return new WalCatalogRead(readback, decode(readback.Data));
            }
        }

        throw new PantsBusyException("Cloud WAL catalog repair exceeded its bounded CAS retries.");
    }

    /// <summary>
    ///     Converges the mirror to <paramref name="primaryBytes" /> after the primary has been
    ///     committed and read back. A lost primary CAS never reaches here, so a losing writer cannot
    ///     publish a mirror.
    /// </summary>
    public async ValueTask ConvergeMirrorAsync(
        ReadOnlyMemory<byte> primaryBytes,
        CancellationToken cancellationToken)
    {
        var mirror = await walStore.GetAsync(
            PantsCloudObjectLayout.WalCatalogMirrorObjectKey,
            cancellationToken).ConfigureAwait(false);
        if (mirror is not null && mirror.Data.Span.SequenceEqual(primaryBytes.Span))
        {
            return;
        }

        await ConvergeMirrorAsync(primaryBytes, mirror, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask ConvergeMirrorAsync(
        ReadOnlyMemory<byte> primaryBytes,
        CloudObject? mirror,
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
            var primary = await walStore.GetAsync(
                PantsCloudObjectLayout.WalCatalogObjectKey,
                cancellationToken).ConfigureAwait(false);
            if (primary is null || !primary.Data.Span.SequenceEqual(primaryBytes.Span))
            {
                return;
            }

            mirror = await walStore.GetAsync(
                PantsCloudObjectLayout.WalCatalogMirrorObjectKey,
                cancellationToken).ConfigureAwait(false);
            if (mirror is not null && mirror.Data.Span.SequenceEqual(primaryBytes.Span))
            {
                return;
            }
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

readonly record struct WalCatalogRead(CloudObject? Object, ProviderWalCatalog? Catalog);
