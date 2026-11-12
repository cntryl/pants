using System.Text.Json;
using System.Text.Json.Serialization;
using Cntryl.Pants.Observability;
using Cntryl.Pants.Storage.Internal;
using Cntryl.Pants.Storage.Internal.Wal;

namespace Cntryl.Pants.Cloud.Internal;

sealed class ProviderCloudPersistence : ICloudPersistence
{
    /// <summary>
    ///     How much of a published WAL segment recovery holds in memory at once.
    /// </summary>
    public const int WalRecoveryPageBytes = 4 * 1024 * 1024;
    public const ulong MaximumWholeObjectWalRecoveryBytes = 256UL * 1024 * 1024;
    public const ulong MaximumRetainedWalBytes = 2UL * 1024 * 1024 * 1024;
    public const int MaximumRetainedWalSegments = 16_384;

    const int DdlRegistryReadbackRangeBytes = 64 * 1024;

    static readonly string[] MetadataFiles =
    [
        "manifest.snapshot.json",
        "manifest.json",
        "FORMAT",
        "manifest.journal",
        "intent_log.json"
    ];

    static readonly string[] ManifestMetadataFiles =
    [
        "manifest.snapshot.json",
        "manifest.json",
        "manifest.journal"
    ];

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    readonly ICloudObjectStore _controlStore;
    readonly CloudLeaseCoordinator _lease;
    readonly CloudMetadataAuthority _metadataAuthority;

    readonly string _localRoot;
    readonly CloudSstGarbageCollector _sstGarbageCollector;
    readonly ICloudObjectStore _sstStore;
    readonly ICloudObjectStore _walStore;
    readonly ProviderWalCatalogStore _catalog;
    readonly ResourceBudget _authorityMetadataBudget;
    readonly CloudMetadataWriteAdmission _catalogWriteAdmission;
    readonly WalPublicationAdmission? _walPublicationAdmission;
    readonly ProviderSstPublisher _sstPublisher;
    readonly ProviderWalRetirement _walRetirement;
    readonly ulong _writerEpoch;
    readonly string? _localWalCacheId;
    ulong _minimumLocalWalWriterEpoch;
    readonly ulong _maximumRetainedWalBytes;
    readonly int _maximumRetainedWalSegments;
    readonly WalRetirementDiagnostics _walRetirementDiagnostics;
    Func<IReadOnlyDictionary<string, FileMeta>>? _sstDeletionCandidates;
    Action<FileMeta>? _acknowledgeSstDeletion;
    int _sstDeletionAcknowledgements;
    int _disposed;
    int _persistenceAnomaly;

    public ProviderCloudPersistence(
        string localRoot,
        ICloudObjectStore walStore,
        ICloudObjectStore sstStore,
        ICloudObjectStore controlStore,
        CloudLeaseCoordinator lease,
        IFailpointHandler? failpoints = null,
        SstPublicationAdmission? sstPublicationAdmission = null,
        WalRetirementSettings? walRetirementSettings = null,
        CloudMetadataWriteAdmission? catalogWriteAdmission = null,
        string? localWalCacheId = null,
        WalRetirementDiagnostics? walRetirementDiagnostics = null,
        ulong maximumRetainedWalBytes = MaximumRetainedWalBytes,
        int maximumRetainedWalSegments = MaximumRetainedWalSegments,
        WalPublicationAdmission? walPublicationAdmission = null,
        ProviderSstIdentityRegistry? identityRegistry = null)
    {
        ArgumentOutOfRangeException.ThrowIfZero(maximumRetainedWalBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRetainedWalSegments);
        _localRoot = Path.GetFullPath(localRoot);
        _walStore = walStore;
        var retirementSettings = walRetirementSettings ?? WalRetirementSettings.Standalone();
        _authorityMetadataBudget = retirementSettings.Budget;
        _catalogWriteAdmission = catalogWriteAdmission ??
                                 new CloudMetadataWriteAdmission(retirementSettings.Budget);
        _catalog = new ProviderWalCatalogStore(
            walStore,
            static bytes => DecodeCatalog(bytes.Span),
            lease.EnsureValid,
            lease.ValidateRemoteAsync,
            new ProviderWalCatalogReadAdmission(retirementSettings.Budget, retirementSettings.Gate),
            _catalogWriteAdmission);
        _sstStore = sstStore;
        _walPublicationAdmission = walPublicationAdmission;
        _sstPublisher = new ProviderSstPublisher(
            sstStore,
            lease.EnsureValid,
            lease.ValidateRemoteAsync,
            sstPublicationAdmission,
            identityRegistry);
        _controlStore = controlStore;
        _lease = lease;
        _metadataAuthority = new CloudMetadataAuthority(controlStore, lease, _authorityMetadataBudget);
        _writerEpoch = lease.Epoch;
        _localWalCacheId = localWalCacheId;
        _maximumRetainedWalBytes = maximumRetainedWalBytes;
        _maximumRetainedWalSegments = maximumRetainedWalSegments;
        _walRetirementDiagnostics = walRetirementDiagnostics ?? new WalRetirementDiagnostics();
        var failpointHandler = failpoints ?? NullPantsFailpointHandler.Instance;
        _sstGarbageCollector = new CloudSstGarbageCollector(
            _sstStore,
            CaptureSstRetentionProofAsync,
            () => CloudSstReferenceReader.ReadLocalProtectedNames(_localRoot),
            _lease.EnsureValid,
            _lease.ValidateRemoteAsync,
            failpointHandler);
        _walRetirement = new ProviderWalRetirement(
            walStore,
            sstStore,
            controlStore,
            _catalog,
            catalog => _catalogWriteAdmission.Serialize(catalog, JsonOptions),
            lease,
            retirementSettings,
            failpointHandler,
            () => Volatile.Write(ref _persistenceAnomaly, 1),
            _walRetirementDiagnostics);
    }

    public bool HasPersistenceAnomaly => Volatile.Read(ref _persistenceAnomaly) != 0;

    public void EnsureReadAuthority() => _lease.EnsureValid();

    public ulong WriterEpoch => _writerEpoch;

    public void BindSstDeletionAuthority(
        Func<IReadOnlyDictionary<string, FileMeta>> candidates, Action<FileMeta> acknowledge)
    {
        _sstDeletionCandidates = candidates;
        _acknowledgeSstDeletion = acknowledge;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _metadataAuthority.Dispose();
        await ProviderObjectStoreSet.DisposeDistinctAsync(
                [_walStore, _sstStore, _controlStore])
            .ConfigureAwait(false);

        GC.SuppressFinalize(this);
    }

    public async ValueTask PublishWalBatchAsync(
        IReadOnlyList<SealedWalSegment> segments,
        CancellationToken cancellationToken)
    {
        if (segments.Count == 0)
        {
            return;
        }

        // Validate every segment at its upload boundary. Revalidation after all uploads and
        // immediately before each catalog write catches takeovers during provider operations.
        var batchEpoch = segments[0].WriterEpoch;
        if (segments.Any(segment => segment.WriterEpoch != batchEpoch))
        {
            throw PantsException.InvalidArgument("A cloud WAL batch cannot cross writer epochs.");
        }

        if (batchEpoch > _writerEpoch)
        {
            throw new PantsFencedException("A WAL segment from a future cloud lease cannot be published.");
        }

        for (var index = 1; index < segments.Count; index++)
        {
            if (segments[index].SegmentId <= segments[index - 1].SegmentId)
            {
                throw new PantsCorruptionException(
                    "A cloud WAL publication batch must contain increasing segment identities.");
            }
        }

        var publications = new ProviderPublishedWalSegment[segments.Count];
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            using var publicationReservation = _walPublicationAdmission is null
                ? null
                : _walPublicationAdmission.Admit(segment.Bytes.LongLength);
            await _lease.ValidateRemoteAsync(cancellationToken).ConfigureAwait(false);
            var objectKey = PantsCloudObjectLayout.WalSegmentObjectKey(
                segment.WriterEpoch,
                segment.SegmentId);
            var created = await _walStore.PutAsync(
                objectKey,
                segment.Bytes,
                new PantsCloudObjectWriteCondition.IfAbsent(),
                cancellationToken).ConfigureAwait(false);
            var remoteSegment = await _walStore.GetAsync(objectKey, cancellationToken)
                .ConfigureAwait(false) ?? throw new PantsLeaseIndeterminateException(
                "The immutable WAL upload was acknowledged without an authoritative object.");
            _lease.EnsureValid();
            if (!remoteSegment.Data.Span.SequenceEqual(segment.Bytes))
            {
                throw created
                    ? new PantsCorruptionException(
                        "The immutable WAL upload read back different bytes.")
                    : new PantsFencedException(
                        "The cloud WAL object conflicts with this writer epoch.");
            }

            publications[index] = new ProviderPublishedWalSegment
            {
                Previous = segment.Header.Prev,
                Facts = segment.Facts,
                SegmentId = segment.SegmentId,
                WriterEpoch = segment.WriterEpoch,
                MaximumSequence = segment.MaximumSequence,
                SizeBytes = checked((ulong)segment.Bytes.Length),
                ContentCrc32C = DiskFormat.Crc32C(segment.Bytes),
                ObjectKey = objectKey
            };
        }

        // Segment objects are immutable and epoch-scoped, so a takeover during upload can leave
        // unreferenced objects behind. Re-read ownership before changing the catalog that makes
        // those objects authoritative or allowing the runtime to acknowledge the batch.
        await _lease.ValidateRemoteAsync(cancellationToken).ConfigureAwait(false);

        for (var attempt = 0; attempt < 8; attempt++)
        {
            using var read = await _catalog.ReadAsync(
                    false,
                    cancellationToken,
                    waitForMaintenanceGate: false)
                .ConfigureAwait(false);
            _lease.EnsureValid();
            var currentVersion = read.Version;
            var catalog = read.Catalog ?? new ProviderWalCatalog { FencingEpoch = _writerEpoch };
            if (catalog.FencingEpoch != _writerEpoch)
            {
                throw new PantsFencedException("The cloud WAL catalog is not fenced to this writer.");
            }

            var updatedSegments = new SortedDictionary<ulong, ProviderPublishedWalSegment>(catalog.Segments);
            var nextSegmentId = catalog.NextSegmentId;
            var walHistoryStartSegmentId = catalog.WalHistoryStartSegmentId;
            if (nextSegmentId == 0)
            {
                nextSegmentId = catalog.Segments.Count == 0
                    ? await ReadNextWalSegmentIdAsync(cancellationToken).ConfigureAwait(false)
                    : checked(catalog.Segments.Keys.Max() + 1);
            }

            var changed = false;
            foreach (var publication in publications)
            {
                if (catalog.Segments.TryGetValue(publication.SegmentId, out var publishedSegment))
                {
                    if (!SameWalPublication(publishedSegment, publication))
                    {
                        throw new PantsCorruptionException(
                            $"Cloud WAL catalog segment {publication.SegmentId} conflicts with recovered bytes.");
                    }

                    continue;
                }

                if (publication.SegmentId < nextSegmentId)
                {
                    if (publication.SegmentId <= catalog.Anchor.Id)
                    {
                        continue;
                    }

                    throw new PantsCorruptionException("A WAL publication reuses an allocated segment identity.");
                }

                var predecessor = updatedSegments.Count == 0 ? catalog.Anchor : updatedSegments.Values.Last().Facts!.Link;
                if (publication.Previous is null || !publication.Previous.Matches(predecessor))
                {
                    throw new PantsCorruptionException("A WAL publication does not link the catalog head.");
                }

                if (publication.WriterEpoch < Math.Max(catalog.MinimumLocalWalWriterEpoch, _minimumLocalWalWriterEpoch))
                {
                    throw new PantsFencedException(
                        $"Predecessor-only WAL segment {publication.SegmentId} is outside the shared cache recovery authority.");
                }

                updatedSegments.Add(publication.SegmentId, publication);
                nextSegmentId = checked(nextSegmentId + 1);
                changed = true;
            }

            if (!changed)
            {
                await _lease.ValidateRemoteAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (updatedSegments.Count > _maximumRetainedWalSegments)
            {
                _walRetirementDiagnostics.SetBlockedCause(
                    PantsWalRetirementBlockedCause.RetainedWalSegmentLimit);
                throw new PantsNoSpaceException(
                    $"Cloud WAL history retention is blocked by " +
                    $"{PantsWalRetirementBlockedCause.RetainedWalSegmentLimit}: " +
                    $"publishing these segments would retain {updatedSegments.Count}, above the " +
                    $"{_maximumRetainedWalSegments}-segment limit.");
            }

            ulong retainedWalBytes;
            try
            {
                retainedWalBytes = updatedSegments.Values.Aggregate(
                    0UL,
                    static (total, segment) => checked(total + segment.SizeBytes));
            }
            catch (OverflowException exception)
            {
                _walRetirementDiagnostics.SetBlockedCause(
                    PantsWalRetirementBlockedCause.RetainedWalByteLimit);
                throw new PantsNoSpaceException(
                    "Cloud WAL history retention exceeded the supported byte accounting range.",
                    exception);
            }

            if (retainedWalBytes > _maximumRetainedWalBytes)
            {
                _walRetirementDiagnostics.SetBlockedCause(
                    PantsWalRetirementBlockedCause.RetainedWalByteLimit);
                throw new PantsNoSpaceException(
                    $"Cloud WAL history retention is blocked by " +
                    $"{PantsWalRetirementBlockedCause.RetainedWalByteLimit}: " +
                    $"publishing these segments would retain {retainedWalBytes} bytes, above the " +
                    $"{_maximumRetainedWalBytes}-byte limit.");
            }

            using var serializedCatalog = _catalogWriteAdmission.Serialize(
                catalog with
                {
                    FencingEpoch = _writerEpoch,
                    NextSegmentId = nextSegmentId,
                    WalHistoryStartSegmentId = walHistoryStartSegmentId,
                    Segments = updatedSegments
                },
                JsonOptions);
            var bytes = serializedCatalog.Bytes;
            _ = DecodeCatalog(bytes.Span);
            await _lease.ValidateRemoteAsync(cancellationToken).ConfigureAwait(false);
            var published = await _walStore.PutAsync(
                PantsCloudObjectLayout.WalCatalogObjectKey,
                bytes,
                currentVersion is null
                    ? new PantsCloudObjectWriteCondition.IfAbsent()
                    : new PantsCloudObjectWriteCondition.IfVersion(currentVersion),
                cancellationToken).ConfigureAwait(false);
            if (published)
            {
                var readback = await _walStore.GetAsync(
                    PantsCloudObjectLayout.WalCatalogObjectKey,
                    cancellationToken).ConfigureAwait(false);
                if (readback is not null && readback.Data.Span.SequenceEqual(bytes.Span))
                {
                    await _catalog.ConvergeMirrorAsync(bytes, readback.Version, cancellationToken)
                        .ConfigureAwait(false);
                    _lease.EnsureValid();
                    _walRetirementDiagnostics.SetBlockedCause(
                        PantsWalRetirementBlockedCause.None);
                    return;
                }

                throw new PantsCorruptionException(
                    "The cloud WAL publication catalog read back different bytes after CAS.");
            }
        }

        throw new PantsBusyException("Cloud WAL catalog publication exceeded its bounded CAS retries.");
    }

    public async ValueTask MirrorMetadataAndSstsAsync(CancellationToken cancellationToken)
    {
        using var receipt = await MirrorMetadataAndSstsWithReceiptAsync(0, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CloudMetadataPublicationReceipt> MirrorMetadataAndSstsWithReceiptAsync(ulong accountingOwnerId, CancellationToken cancellationToken)
    {
        _lease.EnsureValid();
        var metadata = CloudControlMetadataSnapshot.Capture(
            _localRoot,
            MetadataFiles,
            _authorityMetadataBudget);
        try
        {
            await EnsureRemoteManifestNotAheadAsync(metadata, false, cancellationToken).ConfigureAwait(false);
            await PublishCapturedSstsAsync(metadata, cancellationToken).ConfigureAwait(false);

            await _metadataAuthority.PublishAsync(metadata, cancellationToken).ConfigureAwait(false);

            await CollectObsoleteSstsAsync(cancellationToken).ConfigureAwait(false);
            return new CloudMetadataPublicationReceipt(metadata, accountingOwnerId);
        }
        catch
        {
            metadata.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Publishes recovery checkpoint outputs with exact, identity-pinned byte verification
    ///     before making their manifest authoritative. Recovery may evict these local files only
    ///     after this method completes.
    /// </summary>
    public async ValueTask PublishRecoveryCheckpointAsync(
        IReadOnlyList<string> sstNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sstNames);
        foreach (var name in sstNames)
        {
            var localPath = Path.Combine(_localRoot, "sst", ValidateSstName(name));
            if (!File.Exists(localPath))
            {
                throw new PantsRecoveryFailedException(
                    $"Recovery checkpoint SST '{name}' is missing before cloud publication.");
            }

            await _sstPublisher.PublishOutputAsync(name, localPath, cancellationToken)
                .ConfigureAwait(false);
        }

        await MirrorMetadataAndSstsAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask ReconcileRecoveredMetadataAsync(CancellationToken cancellationToken) =>
        MirrorMetadataAndSstsAsync(cancellationToken);

    public ValueTask<WalRetirementResult> RetireCoveredWalAsync(CancellationToken cancellationToken) =>
        _walRetirement.RetireAsync(cancellationToken);

    public async ValueTask CollectObsoleteSstsAsync(CancellationToken cancellationToken)
    {
        if (!await _sstGarbageCollector.CollectAsync(cancellationToken).ConfigureAwait(false))
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
        }

        var acknowledgements = Volatile.Read(ref _sstDeletionAcknowledgements);
        if (acknowledgements != 0)
        {
            using var metadata = CloudControlMetadataSnapshot.Capture(_localRoot, MetadataFiles, _authorityMetadataBudget);
            await _metadataAuthority.PublishAsync(metadata, cancellationToken).ConfigureAwait(false);
            Interlocked.CompareExchange(ref _sstDeletionAcknowledgements, 0, acknowledgements);
        }
    }

    public async ValueTask ValidateWriteAuthorityAsync(CancellationToken cancellationToken)
    {
        _lease.EnsureValid();
        using var metadata = CloudControlMetadataSnapshot.Capture(
            _localRoot,
            ManifestMetadataFiles,
            _authorityMetadataBudget);
        await EnsureRemoteManifestNotAheadAsync(metadata, true, cancellationToken).ConfigureAwait(false);
        _lease.EnsureValid();
    }

    public async ValueTask<CloudDdlRegistryObject?> ReadDdlRegistryAsync(
        CancellationToken cancellationToken)
    {
        _lease.EnsureValid();
        using var reads = new ProviderCloudControlMetadataReadSet(
            _controlStore,
            _authorityMetadataBudget);
        var current = await reads.ReadAsync(
            PantsCloudObjectLayout.DdlRegistryObjectKey,
            cancellationToken).ConfigureAwait(false);
        _lease.EnsureValid();
        if (current is null)
        {
            return null;
        }

        var reservation = reads.TransferReservations();
        try
        {
            return new CloudDdlRegistryObject(
                CloudDdlJson.DeserializeRegistry(current.Data.Span),
                current.Version,
                current.Data,
                reservation);
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    public async ValueTask FenceDdlRegistryAsync(
        CloudDdlRegistry bootstrap,
        CancellationToken cancellationToken)
    {
        _lease.EnsureValid();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            using var current = await ReadDdlRegistryAsync(cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                CloudDdlJson.ValidateRegistry(bootstrap);
            }

            using var bootstrapBytes = current is null
                ? _catalogWriteAdmission.Serialize(
                    bootstrap,
                    CloudDdlJson.Options,
                    "Cloud DDL registry")
                : null;
            var baseline = current?.Data ?? bootstrapBytes!.Bytes;
            using var publicationReservation = _catalogWriteAdmission.AdmitExisting(
                checked((long)baseline.Length + (sizeof(ulong) * 8) + 2),
                "Cloud DDL registry fence");
            var fencedBytes = CloudDdlFence.Encode(baseline.Span, _writerEpoch);
            if (current?.Data.Span.SequenceEqual(fencedBytes) == true)
            {
                return;
            }

            await _lease.ValidateRemoteAsync(cancellationToken).ConfigureAwait(false);
            var published = await _controlStore.PutAsync(
                PantsCloudObjectLayout.DdlRegistryObjectKey,
                fencedBytes,
                current is null
                    ? new PantsCloudObjectWriteCondition.IfAbsent()
                    : new PantsCloudObjectWriteCondition.IfVersion(current.Version),
                cancellationToken).ConfigureAwait(false);
            _lease.EnsureValid();
            if (!published)
            {
                continue;
            }

            await VerifyDdlRegistryReadbackAsync(fencedBytes, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        throw new PantsBusyException(
            "Cloud DDL registry fencing exceeded its bounded CAS retries.");
    }

    public async ValueTask<bool> CompareExchangeDdlRegistryAsync(
        CloudDdlRegistry registry,
        string? expectedVersion,
        CancellationToken cancellationToken,
        Action? beforeSubmission = null)
    {
        _lease.EnsureValid();
        registry.WriterEpoch = _writerEpoch;
        CloudDdlJson.ValidateRegistry(registry);
        using var data = _catalogWriteAdmission.Serialize(
            registry,
            CloudDdlJson.Options,
            "Cloud DDL registry");
        beforeSubmission?.Invoke();
        await _lease.ValidateRemoteAsync(cancellationToken).ConfigureAwait(false);
        _lease.EnsureValid();
        var published = await _controlStore.PutAsync(
            PantsCloudObjectLayout.DdlRegistryObjectKey,
            data.Bytes,
            expectedVersion is null
                ? new PantsCloudObjectWriteCondition.IfAbsent()
                : new PantsCloudObjectWriteCondition.IfVersion(expectedVersion),
            cancellationToken).ConfigureAwait(false);
        _lease.EnsureValid();
        if (!published)
        {
            return false;
        }

        await VerifyDdlRegistryReadbackAsync(data.Bytes, cancellationToken).ConfigureAwait(false);
        return true;
    }

    async ValueTask VerifyDdlRegistryReadbackAsync(
        ReadOnlyMemory<byte> expectedBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            await VerifyDdlRegistryReadbackCoreAsync(expectedBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PantsResourceLimitException exception)
        {
            throw new PantsLeaseIndeterminateException(
                "The cloud DDL registry may have been published but could not be verified within " +
                "the resource envelope.",
                exception);
        }
    }

    async ValueTask VerifyDdlRegistryReadbackCoreAsync(
        ReadOnlyMemory<byte> expectedBytes,
        CancellationToken cancellationToken)
    {
        const string objectKey = PantsCloudObjectLayout.DdlRegistryObjectKey;
        var metadata = await _controlStore.HeadAsync(objectKey, cancellationToken)
            .ConfigureAwait(false) ?? throw new PantsLeaseIndeterminateException(
            "The cloud DDL registry publication was acknowledged without an authoritative object.");
        _lease.EnsureValid();
        if (metadata.SizeBytes != checked((ulong)expectedBytes.Length))
        {
            throw new PantsCorruptionException(
                "The cloud DDL registry readback has a different byte length.");
        }

        if (expectedBytes.IsEmpty)
        {
            return;
        }

        for (var offset = 0; offset < expectedBytes.Length;)
        {
            var length = Math.Min(DdlRegistryReadbackRangeBytes, expectedBytes.Length - offset);
            CloudObject? range;
            try
            {
                range = _controlStore is CloudObjectStore builtInStore
                    ? await builtInStore.GetRangeAsync(
                            objectKey,
                            checked((ulong)offset),
                            length,
                            metadata,
                            cancellationToken)
                        .ConfigureAwait(false)
                    : await _controlStore.GetRangeAsync(
                            objectKey,
                            checked((ulong)offset),
                            length,
                            cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (PantsNotSupportedException exception)
            {
                if (metadata.SizeBytes > ProviderCloudControlMetadataReadSet.MaximumWholeObjectBytes)
                {
                    throw new PantsResourceLimitException(
                        "The cloud DDL registry is too large for a store without bounded ranged reads.",
                        exception);
                }

                range = await _controlStore.GetAsync(objectKey, cancellationToken).ConfigureAwait(false);
                _lease.EnsureValid();
                if (range is null || !StringComparer.Ordinal.Equals(range.Version, metadata.Version) ||
                    range.Data.Length != expectedBytes.Length)
                {
                    throw new PantsBusyException(
                        "The cloud DDL registry changed during publication readback.");
                }

                if (!range.Data.Span.SequenceEqual(expectedBytes.Span))
                {
                    throw new PantsCorruptionException(
                        "The cloud DDL registry read back different bytes after publication.");
                }

                return;
            }

            _lease.EnsureValid();
            if (range is null || !StringComparer.Ordinal.Equals(range.Version, metadata.Version))
            {
                throw new PantsBusyException(
                    "The cloud DDL registry changed during publication readback.");
            }

            if (range.Data.Length != length)
            {
                throw new PantsIOException(
                    "The cloud DDL registry did not return the requested readback range.");
            }

            if (!range.Data.Span.SequenceEqual(expectedBytes.Span.Slice(offset, length)))
            {
                throw new PantsCorruptionException(
                    "The cloud DDL registry read back different bytes after publication.");
            }

            offset += length;
        }
    }

    public static async ValueTask<ProviderCloudHydrationResult> HydrateLocalCacheAsync(
        string localRoot,
        ICloudObjectStore walStore,
        ICloudObjectStore sstStore,
        ICloudObjectStore controlStore,
        PantsRecoveryPolicy recoveryPolicy,
        Func<CancellationToken, ValueTask> validateRemoteLease,
        CancellationToken cancellationToken,
        ulong maximumWholeObjectBytes = MaximumWholeObjectWalRecoveryBytes,
        ProviderWalCatalogReadAdmission? catalogReadAdmission = null,
        CloudMetadataWriteAdmission? catalogWriteAdmission = null,
        ResourceBudget? controlMetadataMemoryBudget = null,
        RecoveryWorkSink? recoveryWork = null)
    {
        using var controlMetadataReads = new ProviderCloudControlMetadataReadSet(
            controlStore,
            controlMetadataMemoryBudget ?? new ResourceBudget(64L * 1024 * 1024),
            recoveryWork);
        await validateRemoteLease(cancellationToken).ConfigureAwait(false);
        var root = Path.GetFullPath(localRoot);
        Directory.CreateDirectory(root);
        // Capture catalog retirement authority before staging any selected control bytes. An
        // older metadata generation cannot recover history whose WAL prefix has been removed.
        var catalogStore = new ProviderWalCatalogStore(
                walStore,
                static bytes => DecodeCatalog(bytes.Span),
                static () => { },
                validateRemoteLease,
                catalogReadAdmission,
                catalogWriteAdmission,
                recoveryWork);
        ulong retirementGeneration;
        using (var authority = await catalogStore.ReadAsync(true, cancellationToken, repairCopies: false).ConfigureAwait(false))
        {
            retirementGeneration = authority.Catalog?.RetiredBy ?? 0;
        }

        var selectedFiles = await CloudMetadataAuthority.ReadCommittedFilesAsync(controlStore, cancellationToken,
            readSet: controlMetadataReads, minimumGeneration: retirementGeneration).ConfigureAwait(false);
        var nilCommittedPointer = selectedFiles is { Count: 0 };
        if (nilCommittedPointer)
        {
            CloudInitializationCacheValidator.Validate(root);
            selectedFiles = null;
        }

        // Repair is a mutation: only attempt it after the selected control bodies have been
        // admitted and validated. A changed retirement cut requires a fresh metadata selection.
        using var catalogRead = await catalogStore.ReadAsync(true, cancellationToken).ConfigureAwait(false);
        var catalog = catalogRead.Catalog;
        if ((catalog?.RetiredBy ?? 0) != retirementGeneration)
        {
            throw new PantsBusyException("Cloud WAL retirement authority changed during metadata hydration.");
        }

        if (nilCommittedPointer && catalog is { Segments.Count: > 0 })
        {
            throw new PantsCorruptionException("A nil committed cloud pointer cannot authorize a published WAL catalog history.");
        }

        var metadataSalvage = false;
        if (selectedFiles is not null)
        {
            foreach (var fileName in MetadataFiles)
            {
                var localPath = Path.Combine(root, fileName);
                await validateRemoteLease(cancellationToken).ConfigureAwait(false);
                if (selectedFiles.TryGetValue(fileName, out var bytes))
                {
                    AtomicStagedFile.Write(localPath, bytes.Span);
                }
                else if (File.Exists(localPath))
                {
                    File.Delete(localPath);
                }
            }
        }

        var localManifest = CloudManifestReader.ReadManifest(root);
        var remoteManifest = localManifest;
        var useRemoteMetadata = selectedFiles is not null;
        var activeManifest = localManifest;
        var remoteNextWalSegmentId = activeManifest?.WalHistoryStart ?? 1;

        var inventoryWork = recoveryWork?.Begin("sst_inventory");
        foreach (var file in activeManifest?.Files ?? [])
        {
            var remote = await sstStore.HeadAsync(
                PantsCloudObjectLayout.SstPrefix + file.Name,
                cancellationToken).ConfigureAwait(false);
            var localPath = Path.Combine(root, "sst", file.Name);
            if (remote is null)
            {
                var isRemoteAuthoritative = useRemoteMetadata ||
                                            remoteManifest?.Files.Any(remoteFile =>
                                                StringComparer.Ordinal.Equals(remoteFile.Name, file.Name)) == true;
                if (isRemoteAuthoritative || !File.Exists(localPath))
                {
                    if (recoveryPolicy == PantsRecoveryPolicy.Salvage &&
                        HasVerifiedLocalCopy(localPath, file))
                    {
                        // The only verified replica is the local one; serve it and say so.
                        metadataSalvage = true;
                        continue;
                    }

                    throw new PantsRecoveryFailedException(
                        $"Authoritative cloud SST '{file.Name}' is missing.");
                }

                continue;
            }

            if (file.SizeBytes != 0 && remote.SizeBytes != file.SizeBytes)
            {
                if (recoveryPolicy == PantsRecoveryPolicy.Salvage &&
                    HasVerifiedLocalCopy(localPath, file))
                {
                    metadataSalvage = true;
                    continue;
                }

                throw new PantsCorruptionException(
                    $"Cloud SST '{file.Name}' length differs from its manifest.");
            }

            inventoryWork?.Completed(operations: 1);
        }

        inventoryWork?.Finish();
        remoteManifest = null;
        activeManifest = null;
        localManifest = null;
        controlMetadataReads.ReleaseReservations();

        if (catalog is null)
        {
            return new ProviderCloudHydrationResult(
                new Dictionary<ulong, ProviderPublishedWalSegment>(),
                0,
                metadataSalvage,
                NextWalSegmentId: remoteNextWalSegmentId,
                RequiresBootstrap: nilCommittedPointer);
        }

        var requiresSalvage = metadataSalvage;
        var cloudDurableSequence = metadataSalvage ? 0UL : catalog.Anchor.LastSeq;
        var remoteSegments = new List<IRemoteWalSegment>();
        foreach (var (segmentId, segment) in catalog.Segments)
        {
            ValidateSegment(segmentId, segment, catalog.FencingEpoch);
            var localPath = Path.Combine(
                root,
                "wal",
                $"{segmentId:00000000000000000000}.wal");

            // The common case: verify size and checksum a bounded page at a time and replay the
            // segment straight from remote ranges, so recovery needs no local disk for the backlog
            // and peak memory is one page.
            if (await TryVerifySegmentInPagesAsync(walStore, segment, cancellationToken, recoveryWork: recoveryWork)
                .ConfigureAwait(false))
            {
                remoteSegments.Add(new ProviderRemoteWalSegment(
                    walStore,
                    segment,
                    WalRecoveryPageBytes,
                    recoveryWork));
                if (!requiresSalvage)
                {
                    cloudDurableSequence = Math.Max(
                        cloudDurableSequence,
                        segment.MaximumSequence);
                }

                continue;
            }

            // Without ranged reads, or to salvage, the whole object must be held. Refuse before
            // any GET when it cannot be admitted rather than risk exhausting memory mid-recovery.
            if (segment.SizeBytes > maximumWholeObjectBytes)
            {
                throw new PantsResourceLimitException(
                    $"Published cloud WAL object '{segment.ObjectKey}' is {segment.SizeBytes} bytes, " +
                    $"above the {maximumWholeObjectBytes}-byte limit for whole-object " +
                    "recovery without ranged reads.");
            }

            var remote = await walStore.GetAsync(segment.ObjectKey, cancellationToken)
                .ConfigureAwait(false) ?? throw new PantsRecoveryFailedException(
                $"Published cloud WAL object '{segment.ObjectKey}' is missing.");
            var bytes = remote.Data;
            if (checked((ulong)bytes.Length) != segment.SizeBytes ||
                DiskFormat.Crc32C(remote.Data.Span) != segment.ContentCrc32C)
            {
                if (recoveryPolicy == PantsRecoveryPolicy.Strict)
                {
                    throw new PantsRecoveryFailedException(
                        $"Published cloud WAL object '{segment.ObjectKey}' failed catalog validation.");
                }

                requiresSalvage = true;
                bytes = CloudWalSalvage.CreateLocalRecoveryBytes(bytes.Span);
            }
            else if (!requiresSalvage)
            {
                cloudDurableSequence = Math.Max(
                    cloudDurableSequence,
                    segment.MaximumSequence);
            }

            await validateRemoteLease(cancellationToken).ConfigureAwait(false);
            AtomicStagedFile.Write(localPath, bytes.Span);
            remoteSegments.Add(new DownloadedWalSegment(segmentId, localPath));
        }

        return new ProviderCloudHydrationResult(
            catalog.Segments,
            cloudDurableSequence,
            requiresSalvage,
            remoteSegments,
            catalog.NextSegmentId == 0 ? remoteNextWalSegmentId : catalog.NextSegmentId,
            catalog.Segments.Count == 0 ? catalog.Anchor : catalog.Segments.Values.Last().Facts!.Link,
            Math.Max(catalog.SequenceFloor, cloudDurableSequence),
            RequiresBootstrap: nilCommittedPointer);
    }

    /// <summary>
    ///     Verifies a published WAL segment's size and checksum a page at a time without keeping it.
    /// </summary>
    /// <remarks>
    ///     Reports <see langword="false" /> rather than throwing when the segment cannot be verified
    ///     this way: the store may decline ranged reads, which the public object-store contract
    ///     permits, or the bytes may not match the catalog. The caller then takes the whole-object
    ///     path, which also carries the salvage handling for a mismatch.
    /// </remarks>
    static bool HasVerifiedLocalCopy(string localPath, FileMeta file)
    {
        if (!File.Exists(localPath) || (file.SizeBytes == 0 && !file.ContentCrc32C.HasValue))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                localPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (file.SizeBytes != 0 && (ulong)stream.Length != file.SizeBytes)
            {
                return false;
            }

            if (file.ContentCrc32C is not { } expected)
            {
                return true;
            }

            var checksum = 0U;
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                checksum = DiskFormat.Crc32CAppend(checksum, buffer.AsSpan(0, read));
            }

            return checksum == expected;
        }
        catch (IOException)
        {
            return false;
        }
    }

    static async ValueTask<bool> TryVerifySegmentInPagesAsync(
        ICloudObjectStore walStore,
        ProviderPublishedWalSegment segment,
        CancellationToken cancellationToken,
        int pageBytes = WalRecoveryPageBytes,
        RecoveryWorkSink? recoveryWork = null)
    {
        var progress = recoveryWork?.Begin("crc_validation");
        try
        {
            var metadata = await walStore.HeadAsync(segment.ObjectKey, cancellationToken)
                .ConfigureAwait(false);
            if (metadata is null || metadata.SizeBytes != segment.SizeBytes)
            {
                return false;
            }

            var checksum = 0U;
            var read = 0UL;
            while (read < segment.SizeBytes)
            {
                var length = (int)Math.Min((ulong)pageBytes, segment.SizeBytes - read);
                var page = await walStore
                    .GetRangeAsync(segment.ObjectKey, read, length, cancellationToken)
                    .ConfigureAwait(false);
                if (page is null || page.Data.Length != length)
                {
                    return false;
                }

                checksum = DiskFormat.Crc32CAppend(checksum, page.Data.Span);
                read = checked(read + (ulong)length);
                recoveryWork?.Record(new RecoveryWorkCompleted("remote_read", length, 0, 0));
                progress?.Completed(length);
            }

            if (checksum != segment.ContentCrc32C)
            {
                return false;
            }

            progress?.Finish();
            return true;
        }
        catch (PantsNotSupportedException)
        {
            return false;
        }
    }

    WalCacheAuthority? ReadWalCacheAuthority()
    {
        var path = Path.Combine(_localRoot, ".cloud-wal-authority");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WalCacheAuthority>(File.ReadAllBytes(path), JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new PantsCorruptionException("The local WAL cache continuity record is invalid.", exception);
        }
    }

    void RecordWalCacheAuthority(ulong minimumWriterEpoch)
    {
        if (_localWalCacheId is not null)
        {
            AtomicStagedFile.Write(Path.Combine(_localRoot, ".cloud-wal-authority"),
                JsonSerializer.SerializeToUtf8Bytes(new WalCacheAuthority(_localWalCacheId, _writerEpoch, minimumWriterEpoch), JsonOptions));
        }
    }

    internal static bool SameWalPublication(ProviderPublishedWalSegment left, ProviderPublishedWalSegment right) =>
        left.SegmentId == right.SegmentId && left.WriterEpoch == right.WriterEpoch &&
        left.MaximumSequence == right.MaximumSequence && left.SizeBytes == right.SizeBytes &&
        left.ContentCrc32C == right.ContentCrc32C && left.ObjectKey == right.ObjectKey &&
        left.Previous is not null && right.Previous is not null && left.Previous.Matches(right.Previous) &&
        left.Facts is not null && right.Facts is not null && left.Facts.Link.Matches(right.Facts.Link) &&
        left.Facts.RecordCount == right.Facts.RecordCount && left.Facts.DataLen == right.Facts.DataLen &&
        left.Facts.MaxSeqPerCf.Length == right.Facts.MaxSeqPerCf.Length &&
        left.Facts.MaxSeqPerCf.Where((row, index) => !row.AsSpan().SequenceEqual(right.Facts.MaxSeqPerCf[index])).Any() == false;

    public async ValueTask<bool> FenceWalCatalogAsync(CancellationToken cancellationToken)
    {
        _lease.EnsureValid();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            using var read = await _catalog.ReadAsync(false, cancellationToken).ConfigureAwait(false);
            var currentVersion = read.Version;
            var catalog = read.Catalog ?? new ProviderWalCatalog();
            if (catalog.FencingEpoch > _writerEpoch)
            {
                throw new PantsFencedException("The cloud WAL catalog has a newer fencing epoch.");
            }

            var cacheAuthority = ReadWalCacheAuthority();
            var canRecoverLocalTail = _localWalCacheId is null || catalog.FencingEpoch == 0 ||
                cacheAuthority is not null && cacheAuthority.CacheId == _localWalCacheId &&
                cacheAuthority.CatalogEpoch == catalog.FencingEpoch;
            var minimumLocalWalWriterEpoch = canRecoverLocalTail
                ? cacheAuthority?.MinimumWriterEpoch ?? 0
                : _writerEpoch;
            _minimumLocalWalWriterEpoch = minimumLocalWalWriterEpoch;

            var nextSegmentId = catalog.NextSegmentId;
            if (nextSegmentId == 0)
            {
                nextSegmentId = catalog.Segments.Count == 0
                    ? await ReadNextWalSegmentIdAsync(cancellationToken).ConfigureAwait(false)
                    : checked(catalog.Segments.Keys.Max() + 1);
            }

            var updatedCatalog = catalog with
            {
                FencingEpoch = _writerEpoch,
                NextSegmentId = nextSegmentId,
                LocalWalCacheId = _localWalCacheId ?? catalog.LocalWalCacheId,
                LocalWalCacheEpoch = _localWalCacheId is null
                    ? catalog.LocalWalCacheEpoch
                    : _writerEpoch,
                MinimumLocalWalWriterEpoch = minimumLocalWalWriterEpoch
            };
            if (catalog.FencingEpoch == updatedCatalog.FencingEpoch &&
                catalog.NextSegmentId == updatedCatalog.NextSegmentId &&
                StringComparer.Ordinal.Equals(catalog.LocalWalCacheId, updatedCatalog.LocalWalCacheId) &&
                catalog.LocalWalCacheEpoch == updatedCatalog.LocalWalCacheEpoch &&
                catalog.MinimumLocalWalWriterEpoch == updatedCatalog.MinimumLocalWalWriterEpoch)
            {
                RecordWalCacheAuthority(minimumLocalWalWriterEpoch);
                return canRecoverLocalTail;
            }

            using var serializedCatalog = _catalogWriteAdmission.Serialize(
                updatedCatalog,
                JsonOptions);
            var bytes = serializedCatalog.Bytes;
            _ = DecodeCatalog(bytes.Span);
            await _lease.ValidateRemoteAsync(cancellationToken).ConfigureAwait(false);
            var fenced = await _walStore.PutAsync(
                PantsCloudObjectLayout.WalCatalogObjectKey,
                bytes,
                currentVersion is null
                    ? new PantsCloudObjectWriteCondition.IfAbsent()
                    : new PantsCloudObjectWriteCondition.IfVersion(currentVersion),
                cancellationToken).ConfigureAwait(false);
            if (fenced)
            {
                var readback = await _walStore.GetAsync(
                                   PantsCloudObjectLayout.WalCatalogObjectKey,
                                   cancellationToken).ConfigureAwait(false) ??
                               throw new PantsLeaseIndeterminateException(
                                   "The cloud WAL catalog fence was acknowledged without an authoritative object.");
                if (!readback.Data.Span.SequenceEqual(bytes.Span))
                {
                    throw new PantsCorruptionException(
                        "The cloud WAL catalog fence read back different bytes after CAS.");
                }

                await _catalog.ConvergeMirrorAsync(bytes, readback.Version, cancellationToken)
                    .ConfigureAwait(false);
                RecordWalCacheAuthority(minimumLocalWalWriterEpoch);
                return canRecoverLocalTail;
            }
        }

        throw new PantsBusyException("Cloud WAL catalog fencing exceeded its bounded CAS retries.");
    }

    async ValueTask PublishCapturedSstsAsync(
        CloudControlMetadataSnapshot metadata,
        CancellationToken cancellationToken)
    {
        var publishedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var references in metadata.ReferencedSsts
                     .GroupBy(static file => file.Name, StringComparer.Ordinal))
        {
            var name = ValidateSstName(references.Key);
            var path = Path.Combine(_localRoot, "sst", name);
            await _sstPublisher.EnsurePublishedAsync(
                    name,
                    File.Exists(path) ? path : null,
                    references.ToArray(),
                    cancellationToken)
                .ConfigureAwait(false);
            publishedNames.Add(name);
        }

        var sstDirectory = Path.Combine(_localRoot, "sst");
        if (Directory.Exists(sstDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(
                         sstDirectory,
                         "*.sst",
                         SearchOption.TopDirectoryOnly))
            {
                var name = ValidateSstName(Path.GetFileName(path));
                if (publishedNames.Add(name))
                {
                    await _sstPublisher.EnsurePublishedAsync(name, path, [], cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    static string ValidateSstName(string name)
    {
        if (!CloudSstObjectKey.TryGetName(
                PantsCloudObjectLayout.SstPrefix + name,
                out var validatedName) ||
            !StringComparer.Ordinal.Equals(name, validatedName))
        {
            throw new PantsCorruptionException($"Cloud SST name '{name}' is unsafe.");
        }

        return validatedName;
    }

    async ValueTask<CloudSstRetentionProof> CaptureSstRetentionProofAsync(
        CancellationToken cancellationToken)
    {
        using var reads = new ProviderCloudControlMetadataReadSet(_controlStore, _authorityMetadataBudget);
        var pointer = await _controlStore.HeadAsync(CloudMetadataAuthority.PointerKey, cancellationToken).ConfigureAwait(false);
        var files = await CloudMetadataAuthority.ReadCommittedFilesAsync(_controlStore, cancellationToken, readSet: reads).ConfigureAwait(false);
        var protectedNames = new HashSet<string>(CloudSstReferenceReader.ReadLocalProtectedNames(_localRoot), StringComparer.Ordinal);
        var guards = new List<CloudObjectIdentityGuard>();
        var manifests = new List<ManifestState>();
        Dictionary<uint, ulong> nextSequences = [];
        var retiredSsts = new Dictionary<string, FileMeta>(StringComparer.Ordinal);
        var candidates = _sstDeletionCandidates?.Invoke();
        if (files?.ContainsKey("manifest.snapshot.json") == true)
        {
            var manifest = CloudManifestReader.DecodeMetadata(files);
            manifests.Add(manifest);
            CloudSstReferenceReader.AddManifestNames(manifest, protectedNames);
            foreach (var retired in manifest.RetiredSsts)
            {
                if (_acknowledgeSstDeletion is not null && candidates?.TryGetValue(retired.Key, out var candidate) == true &&
                    SameRetiredSst(retired.Value, candidate))
                {
                    retiredSsts.Add(retired.Key, retired.Value.Clone());
                }
                else
                {
                    protectedNames.Add(retired.Key);
                }
            }
            nextSequences = manifest.NextSstSeqs;
        }

        var partialOutputs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (files?.TryGetValue("intent_log.json", out var intent) == true)
        {
            CloudSstReferenceReader.AddIntentNames(intent, protectedNames);
            foreach (var pair in await FindPartialCompactionOutputsAsync(intent, manifests, cancellationToken).ConfigureAwait(false))
            {
                partialOutputs.Add(pair.Key, pair.Value);
            }
        }

        if (pointer is not null)
        {
            guards.Add(new CloudObjectIdentityGuard(_controlStore, CloudMetadataAuthority.PointerKey, pointer.Version));
        }

        return new CloudSstRetentionProof(protectedNames, partialOutputs, nextSequences, guards, reads.TransferReservations(), retiredSsts,
            file =>
            {
                _acknowledgeSstDeletion!(file);
                Interlocked.Increment(ref _sstDeletionAcknowledgements);
            },
            file => _sstDeletionCandidates?.Invoke().TryGetValue(file.Name, out var current) == true && SameRetiredSst(file, current),
            _authorityMetadataBudget);
    }

    static bool SameRetiredSst(FileMeta left, FileMeta right) =>
        left.Name == right.Name && left.Level == right.Level && left.ColumnFamilyId == right.ColumnFamilyId &&
        left.SstSequence == right.SstSequence && left.SizeBytes == right.SizeBytes && left.ContentCrc32C == right.ContentCrc32C &&
        left.SmallestSequence == right.SmallestSequence && left.LargestSequence == right.LargestSequence &&
        left.KeyBoundsComplete == right.KeyBoundsComplete && left.Sublevel == right.Sublevel &&
        left.EarliestExpirationMillis == right.EarliestExpirationMillis &&
        (left.SmallestKey is null ? right.SmallestKey is null : right.SmallestKey is not null && left.SmallestKey.AsSpan().SequenceEqual(right.SmallestKey)) &&
        (left.LargestKey is null ? right.LargestKey is null : right.LargestKey is not null && left.LargestKey.AsSpan().SequenceEqual(right.LargestKey));

    async ValueTask<IReadOnlyDictionary<string, string>> FindPartialCompactionOutputsAsync(
        ReadOnlyMemory<byte> remoteIntent,
        List<ManifestState> remoteManifests,
        CancellationToken cancellationToken)
    {
        var partialOutputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var retainedOutputs = new HashSet<string>(StringComparer.Ordinal);
        if (remoteManifests.Count == 0)
        {
            return partialOutputs;
        }

        foreach (var intent in CloudSstReferenceReader.ReadOutputDurableCompactions(remoteIntent))
        {
            var inputsRemainAuthoritative = remoteManifests.All(manifest => intent.RemovedNames.All(name =>
                manifest.Files.Any(file => StringComparer.Ordinal.Equals(file.Name, name))));
            var outputsRemainUnpublished = remoteManifests.All(manifest => intent.AddedNames.All(name =>
                manifest.Files.All(file => !StringComparer.Ordinal.Equals(file.Name, name))));
            if (!inputsRemainAuthoritative || !outputsRemainUnpublished)
            {
                continue;
            }

            var foundOutputs = new Dictionary<string, CloudObjectMetadata>(StringComparer.Ordinal);
            foreach (var name in intent.AddedNames)
            {
                _lease.EnsureValid();
                var remote = await _sstStore.HeadAsync(
                    PantsCloudObjectLayout.SstPrefix + name,
                    cancellationToken).ConfigureAwait(false);
                _lease.EnsureValid();
                if (remote is not null)
                {
                    foundOutputs.Add(name, remote);
                }
            }

            if (foundOutputs.Count == intent.AddedNames.Count)
            {
                retainedOutputs.UnionWith(intent.AddedNames);
                continue;
            }

            foreach (var (name, output) in foundOutputs)
            {
                if (partialOutputs.TryGetValue(name, out var existingVersion) &&
                    !StringComparer.Ordinal.Equals(existingVersion, output.Version))
                {
                    retainedOutputs.Add(name);
                    continue;
                }

                partialOutputs[name] = output.Version;
            }
        }

        foreach (var name in retainedOutputs)
        {
            partialOutputs.Remove(name);
        }

        return partialOutputs;
    }

    async ValueTask EnsureRemoteManifestNotAheadAsync(
        CloudControlMetadataSnapshot metadata,
        bool recheckCurrentLocal,
        CancellationToken cancellationToken)
    {
        if (metadata.Manifests.Length == 0)
        {
            return;
        }

        using var reads = new ProviderCloudControlMetadataReadSet(_controlStore, _authorityMetadataBudget);
        var files = await CloudMetadataAuthority.ReadCommittedFilesAsync(_controlStore, cancellationToken, readSet: reads).ConfigureAwait(false);
        _lease.EnsureValid();
        if (files?.ContainsKey("manifest.snapshot.json") == true)
        {
            var remoteHorizon = CloudManifestReader.DecodeMetadata(files).EditCheckpointId;
            if (remoteHorizon <= metadata.MaximumManifestSequence)
            {
                return;
            }

            if (recheckCurrentLocal)
            {
                // A concurrent, admitted deletion acknowledgement can publish after this
                // validation captured its local cut. Validate current authority without claiming
                // that the earlier capture itself committed.
                using var current = CloudControlMetadataSnapshot.Capture(_localRoot, ManifestMetadataFiles, _authorityMetadataBudget);
                if (current.MaximumManifestSequence >= remoteHorizon)
                {
                    return;
                }
            }

            throw new PantsFencedException("Committed cloud manifest is ahead of the local cache.");
        }
    }

    internal static ProviderWalCatalog DecodeCatalogForFloor(ReadOnlySpan<byte> bytes) =>
        DecodeCatalog(bytes);

    async ValueTask<ulong> ReadNextWalSegmentIdAsync(CancellationToken cancellationToken)
    {
        using var reads = new ProviderCloudControlMetadataReadSet(_controlStore, _authorityMetadataBudget);
        var files = await CloudMetadataAuthority.ReadCommittedFilesAsync(_controlStore, cancellationToken, readSet: reads).ConfigureAwait(false);
        _lease.EnsureValid();
        return files?.ContainsKey("manifest.snapshot.json") == true
            ? CloudManifestReader.DecodeMetadata(files).WalHistoryStart
            : 1;
    }

    static ProviderWalCatalog DecodeCatalog(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var catalog = JsonSerializer.Deserialize<ProviderWalCatalog>(bytes, JsonOptions) ??
                          throw new JsonException("Catalog is empty.");
            if (catalog.FormatVersion != 3)
            {
                throw new PantsCompatibilityException(
                    $"Cloud WAL catalog version {catalog.FormatVersion} is unsupported.");
            }

            if (catalog.FencingEpoch == 0)
            {
                throw new PantsCorruptionException(
                    "Cloud WAL catalog fencing epoch must be nonzero.");
            }

            if ((catalog.LocalWalCacheId is null && catalog.LocalWalCacheEpoch != 0) ||
                (catalog.LocalWalCacheId is not null &&
                 (catalog.LocalWalCacheId.Length == 0 ||
                  catalog.LocalWalCacheEpoch > catalog.FencingEpoch)) ||
                catalog.MinimumLocalWalWriterEpoch > catalog.FencingEpoch)
            {
                throw new PantsCorruptionException(
                    "Cloud WAL catalog local-cache recovery authority is invalid.");
            }

            foreach (var (segmentId, segment) in catalog.Segments)
            {
                ValidateSegment(segmentId, segment, catalog.FencingEpoch);
            }

            return catalog;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or
                                         InvalidOperationException or FormatException or OverflowException)
        {
            throw new PantsCorruptionException("Cloud WAL catalog is malformed.", exception);
        }
    }

    static void ValidateSegment(
        ulong segmentId,
        ProviderPublishedWalSegment segment,
        ulong fencingEpoch)
    {
        if (segment.SegmentId != segmentId || segment.WriterEpoch > fencingEpoch ||
            segment.SizeBytes == 0 || segment.ObjectKey != PantsCloudObjectLayout.WalSegmentObjectKey(
                segment.WriterEpoch,
                segmentId))
        {
            throw new PantsCorruptionException($"Cloud WAL catalog entry {segmentId} is invalid.");
        }
    }
}
