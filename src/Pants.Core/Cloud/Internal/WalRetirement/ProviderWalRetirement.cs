namespace Cntryl.Pants.Cloud.Internal.WalRetirement;

/// <summary>
///     Retires published WAL segments that the published manifest covers, as one bounded, resumable
///     background maintenance turn.
/// </summary>
/// <remarks>
///     <para>
///         Only the contiguous oldest prefix of the catalog retires, as in Midge: a segment that is
///         not yet covered ends the prefix however many newer ones are, so the catalog never has a
///         gap that recovery would have to replay around.
///     </para>
///     <para>
///         Coverage is proven from manifest bounds while each candidate is read frame by frame; only
///         the SSTs that cover a record are then checked remotely (existence, length and identity),
///         so unrelated SSTs are never touched. Running out of quantum, a provider timeout or busy
///         response, and exhausted maintenance memory all end the turn with the catalog unchanged
///         and proof progress kept. None of these is an error: what is already proven still retires
///         and the rest resumes on a later turn.
///     </para>
/// </remarks>
sealed class ProviderWalRetirement
{
    const int MaximumCatalogAttempts = 8;

    readonly ProviderWalCatalogStore _catalog;
    readonly Func<ProviderWalCatalog, byte[]> _encodeCatalog;
    readonly CloudLeaseCoordinator _lease;
    readonly PublishedManifestReader _manifests;
    readonly Action _markPersistenceAnomaly;
    readonly WalRetirementProgress _progress = new();
    readonly WalSegmentProver _prover;
    readonly WalRetirementSettings _settings;
    readonly ICloudObjectStore _sstStore;
    readonly ICloudObjectStore _walStore;
    readonly ulong _writerEpoch;

    public ProviderWalRetirement(
        ICloudObjectStore walStore,
        ICloudObjectStore sstStore,
        ICloudObjectStore controlStore,
        ProviderWalCatalogStore catalog,
        Func<ProviderWalCatalog, byte[]> encodeCatalog,
        CloudLeaseCoordinator lease,
        WalRetirementSettings settings,
        Action markPersistenceAnomaly)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.PageBytes, 64);
        _walStore = walStore;
        _sstStore = sstStore;
        _catalog = catalog;
        _encodeCatalog = encodeCatalog;
        _lease = lease;
        _writerEpoch = lease.Epoch;
        _settings = settings;
        _markPersistenceAnomaly = markPersistenceAnomaly;
        _manifests = new PublishedManifestReader(controlStore, lease.EnsureValid);
        _prover = new WalSegmentProver(
            walStore,
            new WalRetirementAdmission(settings.Budget, settings.Gate),
            settings.PageBytes,
            lease.EnsureValid);
    }

    /// <summary>Segments with proof progress held for a later turn.</summary>
    internal int ProofsInProgress => _progress.Count;

    public async ValueTask<WalRetirementResult> RetireAsync(CancellationToken cancellationToken)
    {
        var quantum = new WalRetirementQuantum(_settings.TimeProvider, _settings.Quantum);
        var retired = 0;
        for (var attempt = 0; attempt < MaximumCatalogAttempts; attempt++)
        {
            _lease.EnsureValid();
            var turn = await PrepareAsync(quantum, cancellationToken).ConfigureAwait(false);
            if (turn.Proven.Count == 0)
            {
                return new WalRetirementResult(turn.Outcome, retired);
            }

            switch (await CommitAsync(turn, cancellationToken).ConfigureAwait(false))
            {
                case CommitResult.Committed:
                    return new WalRetirementResult(turn.Outcome, retired + turn.Proven.Count);
                case CommitResult.Stale:
                    return new WalRetirementResult(WalRetirementOutcome.Yielded, retired);
                case CommitResult.Deferred:
                    return new WalRetirementResult(WalRetirementOutcome.Deferred, retired);
                case CommitResult.CatalogConflict:
                default:
                    // A WAL publication moved the catalog. Proofs are kept, so the retry only
                    // re-reads the catalog and revalidates.
                    continue;
            }
        }

        return new WalRetirementResult(WalRetirementOutcome.Yielded, retired);
    }

    async ValueTask<RetirementTurn> PrepareAsync(
        WalRetirementQuantum quantum,
        CancellationToken cancellationToken)
    {
        try
        {
            var manifest = await _manifests.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (manifest is null || manifest.LastPersistedSequence == 0)
            {
                return RetirementTurn.Settled;
            }

            var read = await _catalog.ReadAsync(false, cancellationToken).ConfigureAwait(false);
            if (read.Object is not { } current || read.Catalog is not { } catalog)
            {
                return RetirementTurn.Settled;
            }

            if (catalog.FencingEpoch != _writerEpoch)
            {
                throw new PantsFencedException(
                    "The cloud WAL catalog is not fenced to this writer during WAL retirement.");
            }

            _progress.RetainOnly(catalog.Segments.Keys);
            var proven = new List<WalSegmentProof>();
            var outcome = WalRetirementOutcome.Settled;
            foreach (var segment in catalog.Segments.Values.OrderBy(static segment => segment.SegmentId))
            {
                // The contiguous oldest prefix only: the first segment that cannot retire ends it.
                if (segment.MaximumSequence > manifest.LastPersistedSequence)
                {
                    break;
                }

                WalSegmentProofState state;
                WalSegmentProof proof;
                try
                {
                    (state, proof) = await ProveAsync(segment, manifest, quantum, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (IsDeferral(exception, cancellationToken))
                {
                    // What is already proven still retires; this segment resumes next turn.
                    outcome = WalRetirementOutcome.Deferred;
                    break;
                }

                if (state == WalSegmentProofState.Covered)
                {
                    proven.Add(proof);
                    continue;
                }

                if (state is WalSegmentProofState.Yielded or WalSegmentProofState.IdentityChanged)
                {
                    outcome = WalRetirementOutcome.Yielded;
                }

                break;
            }

            return new RetirementTurn(outcome, proven, manifest, current, catalog);
        }
        catch (Exception exception) when (IsDeferral(exception, cancellationToken))
        {
            return RetirementTurn.Deferred;
        }
    }

    async ValueTask<(WalSegmentProofState State, WalSegmentProof Proof)> ProveAsync(
        ProviderPublishedWalSegment segment,
        PublishedManifest manifest,
        WalRetirementQuantum quantum,
        CancellationToken cancellationToken)
    {
        _lease.EnsureValid();
        var metadata = await _walStore.HeadAsync(segment.ObjectKey, cancellationToken)
            .ConfigureAwait(false) ?? throw new PantsRecoveryFailedException(
            $"Published cloud WAL object '{segment.ObjectKey}' is missing during WAL retirement.");
        if (metadata.SizeBytes != segment.SizeBytes)
        {
            throw new PantsCorruptionException(
                $"Published cloud WAL object '{segment.ObjectKey}' differs from its catalog proof.");
        }

        var proof = _progress.Resume(segment, metadata.Version, manifest.Coverage);
        var state = await _prover.ProveAsync(proof, manifest, quantum, cancellationToken)
            .ConfigureAwait(false);
        if (state == WalSegmentProofState.IdentityChanged)
        {
            _progress.Forget(segment.SegmentId);
        }

        return (state, proof);
    }

    async ValueTask<CommitResult> CommitAsync(
        RetirementTurn turn,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await VerifyDependenciesAsync(turn, cancellationToken).ConfigureAwait(false))
            {
                return CommitResult.Stale;
            }
        }
        catch (Exception exception) when (IsDeferral(exception, cancellationToken))
        {
            return CommitResult.Deferred;
        }

        _lease.EnsureValid();
        var catalog = turn.Catalog!;
        var retiredIds = turn.Proven
            .Select(static proof => proof.Segment.SegmentId)
            .ToHashSet();
        var retained = new SortedDictionary<ulong, ProviderPublishedWalSegment>(
            catalog.Segments
                .Where(entry => !retiredIds.Contains(entry.Key))
                .ToDictionary());
        var bytes = _encodeCatalog(catalog with { Segments = retained });
        // From the CAS on, an outcome is no longer a clean deferral: failures surface.
        var published = await _walStore.PutAsync(
            PantsCloudObjectLayout.WalCatalogObjectKey,
            bytes,
            new PantsCloudObjectWriteCondition.IfVersion(turn.CatalogObject!.Version),
            cancellationToken).ConfigureAwait(false);
        if (!published)
        {
            return CommitResult.CatalogConflict;
        }

        var readback = await _walStore.GetAsync(
                           PantsCloudObjectLayout.WalCatalogObjectKey,
                           cancellationToken).ConfigureAwait(false) ??
                       throw new PantsLeaseIndeterminateException(
                           "Cloud WAL catalog retirement was acknowledged without an authoritative object.");
        if (!readback.Data.Span.SequenceEqual(bytes))
        {
            throw new PantsCorruptionException(
                "Cloud WAL catalog retirement read back different bytes after CAS.");
        }

        await _catalog.ConvergeMirrorAsync(bytes, cancellationToken).ConfigureAwait(false);
        _lease.EnsureValid();
        foreach (var proof in turn.Proven)
        {
            _progress.Forget(proof.Segment.SegmentId);
            await TryDeleteRetiredWalAsync(proof, cancellationToken).ConfigureAwait(false);
        }

        return CommitResult.Committed;
    }

    /// <summary>
    ///     Checks, just before the catalog CAS, that each covering SST still exists at its manifest
    ///     length, that each proven WAL object is still the version that was read, and finally that
    ///     the published manifest is unchanged, so the SSTs it references cannot have been collected.
    /// </summary>
    /// <returns>False when something moved; the turn yields and later revalidates.</returns>
    async ValueTask<bool> VerifyDependenciesAsync(
        RetirementTurn turn,
        CancellationToken cancellationToken)
    {
        var coveringFiles = turn.Proven
            .SelectMany(static proof => proof.CoveringFiles.Values)
            .DistinctBy(static file => file.Name, StringComparer.Ordinal);
        foreach (var file in coveringFiles)
        {
            _lease.EnsureValid();
            var remote = await _sstStore.HeadAsync(
                PantsCloudObjectLayout.SstPrefix + file.Name,
                cancellationToken).ConfigureAwait(false);
            if (remote is null)
            {
                if (!await _manifests.IsCurrentAsync(turn.Manifest!.Identity, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return false;
                }

                throw new PantsRecoveryFailedException(
                    $"Manifest cloud SST '{file.Name}' is missing during WAL retirement.");
            }

            if (file.SizeBytes != 0 && remote.SizeBytes != file.SizeBytes)
            {
                throw new PantsCorruptionException(
                    $"Manifest cloud SST '{file.Name}' length differs during WAL retirement.");
            }
        }

        foreach (var proof in turn.Proven)
        {
            _lease.EnsureValid();
            var current = await _walStore.HeadAsync(proof.Segment.ObjectKey, cancellationToken)
                .ConfigureAwait(false);
            if (current is null || !StringComparer.Ordinal.Equals(current.Version, proof.Version))
            {
                _progress.Forget(proof.Segment.SegmentId);
                return false;
            }
        }

        return await _manifests.IsCurrentAsync(turn.Manifest!.Identity, cancellationToken)
            .ConfigureAwait(false);
    }

    async ValueTask TryDeleteRetiredWalAsync(
        WalSegmentProof proof,
        CancellationToken cancellationToken)
    {
        try
        {
            _lease.EnsureValid();
            var outcome = await _walStore.DeleteAsync(
                proof.Segment.ObjectKey,
                new PantsCloudObjectDeleteCondition.IfVersion(proof.Version),
                cancellationToken).ConfigureAwait(false);
            if (outcome != CloudObjectDeleteOutcome.Deleted)
            {
                _markPersistenceAnomaly();
            }
        }
        catch (PantsException) when (!cancellationToken.IsCancellationRequested)
        {
            // The retired catalog is authoritative. Retaining an unproven object as harmless
            // residue is safer than an unconditional retry.
            _markPersistenceAnomaly();
        }
    }

    static bool IsDeferral(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is PantsTimeoutException or PantsBusyException or PantsResourceLimitException;

    enum CommitResult
    {
        Committed,
        CatalogConflict,
        Stale,
        Deferred
    }

    sealed record RetirementTurn(
        WalRetirementOutcome Outcome,
        IReadOnlyList<WalSegmentProof> Proven,
        PublishedManifest? Manifest = null,
        CloudObject? CatalogObject = null,
        ProviderWalCatalog? Catalog = null)
    {
        public static RetirementTurn Settled { get; } = new(WalRetirementOutcome.Settled, []);

        public static RetirementTurn Deferred { get; } = new(WalRetirementOutcome.Deferred, []);
    }
}
