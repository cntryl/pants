namespace Cntryl.Pants.Cloud.Internal.WalRetirement;

/// <summary>
///     Advances a segment proof through version-pinned ranged reads of at most one page (or one
///     frame, when a frame is larger), each admitted against the maintenance pool. Memory is bounded
///     by the page and the largest frame, never by the segment or the number of candidates.
/// </summary>
sealed class WalSegmentProver(
    ICloudObjectStore walStore,
    WalRetirementAdmission admission,
    int pageBytes,
    Action ensureLeaseValid)
{
    bool _wholeObjectReads;

    public async ValueTask<WalSegmentProofState> ProveAsync(
        WalSegmentProof proof,
        PublishedManifest manifest,
        WalRetirementQuantum quantum,
        CancellationToken cancellationToken)
    {
        while (!proof.IsRead)
        {
            if (proof.UncoveredUnder == manifest.Identity)
            {
                return WalSegmentProofState.Uncovered;
            }

            if (quantum.ShouldYield)
            {
                return WalSegmentProofState.Yielded;
            }

            ensureLeaseValid();
            if (!await ReadNextAsync(proof, manifest, cancellationToken).ConfigureAwait(false))
            {
                return WalSegmentProofState.IdentityChanged;
            }

            quantum.RecordProgress();
        }

        proof.EnsureMatchesCatalog();
        return WalSegmentProofState.Covered;
    }

    /// <returns>False when the object is no longer the pinned version.</returns>
    async ValueTask<bool> ReadNextAsync(
        WalSegmentProof proof,
        PublishedManifest manifest,
        CancellationToken cancellationToken)
    {
        if (!_wholeObjectReads)
        {
            try
            {
                return await ReadRangesAsync(proof, manifest, cancellationToken).ConfigureAwait(false);
            }
            catch (PantsNotSupportedException)
            {
                _wholeObjectReads = true;
            }
        }

        return await ReadWholeObjectAsync(proof, manifest, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask<bool> ReadRangesAsync(
        WalSegmentProof proof,
        PublishedManifest manifest,
        CancellationToken cancellationToken)
    {
        var fetch = checked((int)Math.Min(pageBytes, proof.Remaining));
        while (true)
        {
            int required;
            using (await admission.AdmitAsync(fetch, cancellationToken).ConfigureAwait(false))
            {
                var range = await walStore.GetRangeAsync(
                                proof.Segment.ObjectKey,
                                checked((ulong)proof.Offset),
                                fetch,
                                cancellationToken).ConfigureAwait(false) ??
                            throw Missing(proof);
                if (!StringComparer.Ordinal.Equals(range.Version, proof.Version))
                {
                    return false;
                }

                if (range.Data.Length != fetch)
                {
                    throw new PantsCorruptionException(
                        $"Published cloud WAL object '{proof.Segment.ObjectKey}' did not return the " +
                        "requested range.");
                }

                required = WalFrameScanner.Acknowledge(proof, range.Data.Span, manifest);
            }

            if (required == 0)
            {
                return true;
            }

            // The next frame is larger than a page: read exactly that frame.
            fetch = required;
        }
    }

    /// <summary>
    ///     For a store without ranged reads the whole object is the only proof, admitted against the
    ///     pool before the GET; a segment whose workspace exceeds the pool keeps authority.
    /// </summary>
    async ValueTask<bool> ReadWholeObjectAsync(
        WalSegmentProof proof,
        PublishedManifest manifest,
        CancellationToken cancellationToken)
    {
        using var admitted = await admission.AdmitAsync(proof.Length, cancellationToken)
            .ConfigureAwait(false);
        var whole = await walStore.GetAsync(proof.Segment.ObjectKey, cancellationToken)
            .ConfigureAwait(false) ?? throw Missing(proof);
        if (!StringComparer.Ordinal.Equals(whole.Version, proof.Version))
        {
            return false;
        }

        if (whole.Data.Length != proof.Length)
        {
            throw new PantsCorruptionException(
                $"Published cloud WAL object '{proof.Segment.ObjectKey}' differs from its catalog proof.");
        }

        _ = WalFrameScanner.Acknowledge(
            proof,
            whole.Data.Span[checked((int)proof.Offset)..],
            manifest);
        return true;
    }

    static PantsRecoveryFailedException Missing(WalSegmentProof proof) =>
        new($"Published cloud WAL object '{proof.Segment.ObjectKey}' is missing during retirement.");
}
