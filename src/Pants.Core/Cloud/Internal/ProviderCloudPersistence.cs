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
        "manifest.json"
    ];

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    readonly ICloudObjectStore _controlStore;
    readonly CloudLeaseCoordinator _lease;

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
    readonly ulong _maximumRetainedWalBytes;
    readonly int _maximumRetainedWalSegments;
    readonly WalRetirementDiagnostics _walRetirementDiagnostics;
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

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
            if (segments[index].SegmentId != checked(segments[index - 1].SegmentId + 1))
            {
                throw new PantsCorruptionException(
                    "A cloud WAL publication batch must contain consecutive segment identities.");
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

            var firstNewPublication = publications.FirstOrDefault(publication =>
                !catalog.Segments.ContainsKey(publication.SegmentId) &&
                publication.SegmentId >= nextSegmentId);
            if (firstNewPublication is not null &&
                firstNewPublication.SegmentId > nextSegmentId)
            {
                var durableHistoryStart = await ReadNextWalSegmentIdAsync(cancellationToken)
                    .ConfigureAwait(false);
                var nextAfterHistoryPrefix = durableHistoryStart;
                foreach (var segmentId in catalog.Segments.Keys.Where(id => id >= durableHistoryStart))
                {
                    if (segmentId != nextAfterHistoryPrefix)
                    {
                        break;
                    }

                    nextAfterHistoryPrefix = checked(nextAfterHistoryPrefix + 1);
                }

                if (firstNewPublication.SegmentId == nextAfterHistoryPrefix)
                {
                    // The durable manifest covers the skipped identity range. Preserve older
                    // catalog entries for background object retirement while continuing at the
                    // manifest boundary.
                    nextSegmentId = nextAfterHistoryPrefix;
                    walHistoryStartSegmentId = Math.Max(
                        walHistoryStartSegmentId,
                        durableHistoryStart);
                }
                else
                {
                    read.Dispose();
                    var retirement = await _walRetirement.RetireAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (retirement.RetiredSegments == 0)
                    {
                        throw new PantsCorruptionException(
                            $"Cloud WAL segment {firstNewPublication.SegmentId} cannot be published before missing segment {nextSegmentId}.");
                    }

                    continue;
                }
            }

            var changed = false;
            foreach (var publication in publications)
            {
                if (catalog.Segments.TryGetValue(publication.SegmentId, out var publishedSegment))
                {
                    if (publishedSegment != publication)
                    {
                        throw new PantsCorruptionException(
                            $"Cloud WAL catalog segment {publication.SegmentId} conflicts with recovered bytes.");
                    }

                    continue;
                }

                if (publication.SegmentId != nextSegmentId)
                {
                    if (publication.SegmentId < nextSegmentId)
                    {
                        // The catalog's durable identity high-water mark survives prefix
                        // retirement. A stale local copy is already published and retired.
                        continue;
                    }

                    throw new PantsCorruptionException(
                        $"Cloud WAL segment {publication.SegmentId} cannot be published before missing segment {nextSegmentId}.");
                }

                if (publication.WriterEpoch < catalog.MinimumLocalWalWriterEpoch)
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
        _lease.EnsureValid();
        using var metadata = CloudControlMetadataSnapshot.Capture(
            _localRoot,
            MetadataFiles,
            _authorityMetadataBudget);
        await EnsureRemoteManifestNotAheadAsync(metadata, cancellationToken).ConfigureAwait(false);
        await PublishCapturedSstsAsync(metadata, cancellationToken).ConfigureAwait(false);

        foreach (var fileName in MetadataFiles)
        {
            if (metadata.Files.TryGetValue(fileName, out var bytes))
            {
                await PutControlCasAsync(
                    PantsCloudObjectLayout.MetadataPrefix + fileName,
                    bytes,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        await CollectObsoleteSstsAsync(cancellationToken).ConfigureAwait(false);
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

    public async ValueTask ReconcileRecoveredMetadataAsync(CancellationToken cancellationToken)
    {
        try
        {
            _lease.EnsureValid();
            using var metadata = CloudControlMetadataSnapshot.Capture(
                _localRoot,
                MetadataFiles,
                _authorityMetadataBudget);
            var manifestMetadataMatchesRemote = true;
            foreach (var fileName in ManifestMetadataFiles)
            {
                _lease.EnsureValid();
                var remote = await _controlStore.GetAsync(
                    PantsCloudObjectLayout.MetadataPrefix + fileName,
                    cancellationToken).ConfigureAwait(false);
                var localExists = metadata.Files.TryGetValue(fileName, out var local);
                if (localExists != (remote is not null) ||
                    (localExists && !remote!.Data.Span.SequenceEqual(local.Span)))
                {
                    manifestMetadataMatchesRemote = false;
                    break;
                }
            }

            if (!manifestMetadataMatchesRemote)
            {
                await MirrorMetadataAndSstsAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            foreach (var fileName in MetadataFiles)
            {
                if (ManifestMetadataFiles.Contains(fileName, StringComparer.Ordinal) ||
                    !metadata.Files.TryGetValue(fileName, out var local))
                {
                    continue;
                }

                await PutControlCasAsync(
                    PantsCloudObjectLayout.MetadataPrefix + fileName,
                    local,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PantsRecoveryFailedException)
        {
            throw;
        }
        catch (Exception)
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
        }
    }

    public ValueTask<WalRetirementResult> RetireCoveredWalAsync(CancellationToken cancellationToken) =>
        _walRetirement.RetireAsync(cancellationToken);

    public async ValueTask CollectObsoleteSstsAsync(CancellationToken cancellationToken)
    {
        if (!await _sstGarbageCollector.CollectAsync(cancellationToken).ConfigureAwait(false))
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
        }
    }

    public async ValueTask ValidateWriteAuthorityAsync(CancellationToken cancellationToken)
    {
        _lease.EnsureValid();
        using var metadata = CloudControlMetadataSnapshot.Capture(
            _localRoot,
            ManifestMetadataFiles,
            _authorityMetadataBudget);
        await EnsureRemoteManifestNotAheadAsync(metadata, cancellationToken).ConfigureAwait(false);
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
        ResourceBudget? controlMetadataMemoryBudget = null)
    {
        using var controlMetadataReads = new ProviderCloudControlMetadataReadSet(
            controlStore,
            controlMetadataMemoryBudget ?? new ResourceBudget(64L * 1024 * 1024));
        await validateRemoteLease(cancellationToken).ConfigureAwait(false);
        var root = Path.GetFullPath(localRoot);
        Directory.CreateDirectory(root);
        var localManifest = CloudManifestReader.ReadManifest(root);
        var remoteMetadata = new Dictionary<string, CloudObject>(StringComparer.Ordinal);
        var metadataSalvage = false;
        foreach (var fileName in MetadataFiles)
        {
            CloudObject? value;
            try
            {
                value = await controlMetadataReads.ReadAsync(
                    PantsCloudObjectLayout.MetadataPrefix + fileName,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (PantsResourceLimitException)
            {
                // Resource admission is part of authority safety, not a recoverable read error.
                // Salvage must not continue to catalog repair or metadata writes after a rejected
                // control-object read.
                throw;
            }
            catch (PantsException) when (
                recoveryPolicy == PantsRecoveryPolicy.Salvage &&
                !cancellationToken.IsCancellationRequested)
            {
                // Salvage tolerates one unreadable metadata object; Strict does not.
                metadataSalvage = true;
                continue;
            }

            if (value is not null)
            {
                remoteMetadata.Add(fileName, value);
            }
        }

        metadataSalvage |= DropOlderManifestOfTornMetadataSet(remoteMetadata, recoveryPolicy);
        var remoteManifestObject = remoteMetadata.GetValueOrDefault("manifest.snapshot.json") ??
                                   remoteMetadata.GetValueOrDefault("manifest.json");
        var remoteManifest = remoteManifestObject is null
            ? null
            : CloudManifestReader.DecodeManifest(remoteManifestObject.Data.Span);
        var useRemoteMetadata = remoteManifest is not null &&
                                (localManifest is null ||
                                 remoteManifest.LastPersistedSequence > localManifest.LastPersistedSequence);
        foreach (var (fileName, value) in remoteMetadata)
        {
            var localPath = Path.Combine(root, fileName);
            if (useRemoteMetadata || !File.Exists(localPath))
            {
                await validateRemoteLease(cancellationToken).ConfigureAwait(false);
                AtomicStagedFile.Write(localPath, value.Data.Span);
            }
        }

        var activeManifest = useRemoteMetadata ? remoteManifest : localManifest ?? remoteManifest;
        var remoteNextWalSegmentId = activeManifest?.WalHistoryStart ?? 1;

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
        }

        remoteMetadata.Clear();
        remoteManifestObject = null;
        remoteManifest = null;
        activeManifest = null;
        localManifest = null;
        controlMetadataReads.ReleaseReservations();

        using var catalogRead = await new ProviderWalCatalogStore(
                walStore,
                static bytes => DecodeCatalog(bytes.Span),
                static () => { },
                validateRemoteLease,
                catalogReadAdmission,
                catalogWriteAdmission)
            .ReadAsync(true, cancellationToken).ConfigureAwait(false);
        if (catalogRead.Catalog is not { } catalog)
        {
            return new ProviderCloudHydrationResult(
                new Dictionary<ulong, ProviderPublishedWalSegment>(),
                0,
                metadataSalvage,
                NextWalSegmentId: remoteNextWalSegmentId);
        }

        var requiresSalvage = metadataSalvage;
        var cloudDurableSequence = 0UL;
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
            if (await TryVerifySegmentInPagesAsync(walStore, segment, cancellationToken)
                .ConfigureAwait(false))
            {
                remoteSegments.Add(new ProviderRemoteWalSegment(
                    walStore,
                    segment,
                    WalRecoveryPageBytes));
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
        }

        return new ProviderCloudHydrationResult(
            catalog.Segments,
            cloudDurableSequence,
            requiresSalvage,
            remoteSegments,
            catalog.NextSegmentId == 0 ? remoteNextWalSegmentId : catalog.NextSegmentId);
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
    /// <summary>
    ///     Without a journal to reconcile them, <c>manifest.snapshot.json</c> and
    ///     <c>manifest.json</c> must describe the same sequence. Metadata objects are mirrored as
    ///     independent writes, so a crash can leave them apart; Strict refuses to pick one, and
    ///     Salvage keeps the newer and ignores the older.
    /// </summary>
    /// <summary>
    ///     Whether a local SST can stand in for a lost or damaged remote one: it must match the
    ///     manifest's length and, when recorded, its CRC32C. A copy that cannot be proven is not
    ///     used.
    /// </summary>
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

    static bool DropOlderManifestOfTornMetadataSet(
        Dictionary<string, CloudObject> remoteMetadata,
        PantsRecoveryPolicy recoveryPolicy)
    {
        if (remoteMetadata.ContainsKey("manifest.journal") ||
            !remoteMetadata.TryGetValue("manifest.snapshot.json", out var snapshotObject) ||
            !remoteMetadata.TryGetValue("manifest.json", out var manifestObject))
        {
            return false;
        }

        var snapshotSequence = CloudManifestReader.DecodeManifest(snapshotObject.Data.Span)
            .LastPersistedSequence;
        var manifestSequence = CloudManifestReader.DecodeManifest(manifestObject.Data.Span)
            .LastPersistedSequence;
        if (snapshotSequence == manifestSequence)
        {
            return false;
        }

        if (recoveryPolicy == PantsRecoveryPolicy.Strict)
        {
            throw new PantsRecoveryFailedException(
                "Cloud manifest snapshot and manifest.json carry different sequences " +
                $"({snapshotSequence} and {manifestSequence}) and no journal reconciles them.");
        }

        _ = remoteMetadata.Remove(
            snapshotSequence < manifestSequence ? "manifest.snapshot.json" : "manifest.json");
        return true;
    }

    static async ValueTask<bool> TryVerifySegmentInPagesAsync(
        ICloudObjectStore walStore,
        ProviderPublishedWalSegment segment,
        CancellationToken cancellationToken,
        int pageBytes = WalRecoveryPageBytes)
    {
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
            }

            return checksum == segment.ContentCrc32C;
        }
        catch (PantsNotSupportedException)
        {
            return false;
        }
    }

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

            var canRecoverLocalTail = _localWalCacheId is null ||
                                      catalog.LocalWalCacheId is null ||
                                      StringComparer.Ordinal.Equals(
                                          catalog.LocalWalCacheId,
                                          _localWalCacheId);
            var minimumLocalWalWriterEpoch = catalog.MinimumLocalWalWriterEpoch;
            if (_localWalCacheId is not null &&
                catalog.LocalWalCacheId is not null &&
                !StringComparer.Ordinal.Equals(catalog.LocalWalCacheId, _localWalCacheId) &&
                catalog.LocalWalCacheEpoch != 0)
            {
                if (catalog.LocalWalCacheEpoch == ulong.MaxValue)
                {
                    throw new PantsResourceLimitException(
                        "The local WAL cache recovery epoch is exhausted.");
                }

                minimumLocalWalWriterEpoch = Math.Max(
                    minimumLocalWalWriterEpoch,
                    checked(catalog.LocalWalCacheEpoch + 1));
            }

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
                return canRecoverLocalTail;
            }

            using var serializedCatalog = _catalogWriteAdmission.Serialize(
                updatedCatalog,
                JsonOptions);
            var bytes = serializedCatalog.Bytes;
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
        using var metadataReads = new ProviderCloudControlMetadataReadSet(
            _controlStore,
            _authorityMetadataBudget);
        var protectedNames = new HashSet<string>(
            CloudSstReferenceReader.ReadLocalProtectedNames(_localRoot),
            StringComparer.Ordinal);
        var guards = new List<CloudObjectIdentityGuard>(ManifestMetadataFiles.Length + 1);
        var remoteManifests = new List<ManifestState>(ManifestMetadataFiles.Length);
        Dictionary<uint, ulong>? remoteNextSequences = null;
        foreach (var fileName in ManifestMetadataFiles)
        {
            _lease.EnsureValid();
            var objectKey = PantsCloudObjectLayout.MetadataPrefix + fileName;
            var remote = await metadataReads.ReadAsync(objectKey, cancellationToken)
                .ConfigureAwait(false);
            if (remote is null)
            {
                continue;
            }

            var manifest = CloudManifestReader.DecodeManifest(remote.Data.Span);
            remoteManifests.Add(manifest);
            CloudSstReferenceReader.AddManifestNames(manifest, protectedNames);
            remoteNextSequences = IntersectNextSstSequences(
                remoteNextSequences,
                manifest.NextSstSeqs);
            guards.Add(new CloudObjectIdentityGuard(
                _controlStore,
                objectKey,
                remote.Version));
        }

        var partialCompactionOutputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var intentObjectKey = PantsCloudObjectLayout.MetadataPrefix + "intent_log.json";
        var remoteIntent = await metadataReads.ReadAsync(intentObjectKey, cancellationToken)
            .ConfigureAwait(false);
        if (remoteIntent is not null)
        {
            CloudSstReferenceReader.AddIntentNames(remoteIntent.Data, protectedNames);
            guards.Add(new CloudObjectIdentityGuard(
                _controlStore,
                intentObjectKey,
                remoteIntent.Version));
            var outputVersions = await FindPartialCompactionOutputsAsync(
                remoteIntent.Data,
                remoteManifests,
                cancellationToken).ConfigureAwait(false);
            foreach (var (name, version) in outputVersions)
            {
                partialCompactionOutputs.Add(name, version);
            }
        }

        var reservation = metadataReads.TransferReservations();
        try
        {
            return new CloudSstRetentionProof(
                protectedNames,
                partialCompactionOutputs,
                remoteNextSequences ?? new Dictionary<uint, ulong>(),
                guards,
                reservation);
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

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

    static Dictionary<uint, ulong> IntersectNextSstSequences(
        Dictionary<uint, ulong>? current,
        Dictionary<uint, ulong> observed)
    {
        if (current is null)
        {
            return observed.ToDictionary();
        }

        foreach (var familyId in current.Keys.ToArray())
        {
            if (observed.TryGetValue(familyId, out var nextSequence))
            {
                current[familyId] = Math.Min(current[familyId], nextSequence);
            }
            else
            {
                current.Remove(familyId);
            }
        }

        return current;
    }

    async ValueTask PutControlCasAsync(
        string objectKey,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        var current = await _controlStore.GetAsync(objectKey, cancellationToken)
            .ConfigureAwait(false);
        _lease.EnsureValid();
        if (current?.Data.Span.SequenceEqual(data.Span) == true)
        {
            return;
        }

        if (current is not null && IsManifestObjectKey(objectKey))
        {
            var currentManifest = CloudManifestReader.DecodeManifest(current.Data.Span);
            var proposedManifest = CloudManifestReader.DecodeManifest(data.Span);
            if (currentManifest.LastPersistedSequence > proposedManifest.LastPersistedSequence)
            {
                throw new PantsFencedException(
                    $"Cloud manifest '{objectKey}' is ahead of the local publication candidate.");
            }
        }

        await _lease.ValidateRemoteAsync(cancellationToken).ConfigureAwait(false);
        var published = await _controlStore.PutAsync(
            objectKey,
            data,
            current is null
                ? new PantsCloudObjectWriteCondition.IfAbsent()
                : new PantsCloudObjectWriteCondition.IfVersion(current.Version),
            cancellationToken).ConfigureAwait(false);
        if (!published)
        {
            throw new PantsFencedException(
                $"Cloud control object '{objectKey}' lost its conditional publication race.");
        }

        var readback = await _controlStore.GetAsync(objectKey, cancellationToken)
            .ConfigureAwait(false) ?? throw new PantsLeaseIndeterminateException(
            $"Cloud control object '{objectKey}' was acknowledged without an object.");
        _lease.EnsureValid();
        if (!readback.Data.Span.SequenceEqual(data.Span))
        {
            throw new PantsCorruptionException(
                $"Cloud control object '{objectKey}' read back different bytes after CAS.");
        }
    }

    async ValueTask EnsureRemoteManifestNotAheadAsync(
        CloudControlMetadataSnapshot metadata,
        CancellationToken cancellationToken)
    {
        if (metadata.Manifests.Length == 0)
        {
            return;
        }

        foreach (var fileName in ManifestMetadataFiles)
        {
            _lease.EnsureValid();
            var remote = await _controlStore.GetAsync(
                PantsCloudObjectLayout.MetadataPrefix + fileName,
                cancellationToken).ConfigureAwait(false);
            _lease.EnsureValid();
            if (remote is not null &&
                CloudManifestReader.DecodeManifest(remote.Data.Span).LastPersistedSequence >
                metadata.MaximumManifestSequence)
            {
                throw new PantsFencedException(
                    $"Cloud manifest '{fileName}' is ahead of the local cache.");
            }
        }
    }

    static bool IsManifestObjectKey(string objectKey) =>
        objectKey.EndsWith("/manifest.snapshot.json", StringComparison.Ordinal) ||
        objectKey.EndsWith("/manifest.json", StringComparison.Ordinal);

    internal static ProviderWalCatalog DecodeCatalogForFloor(ReadOnlySpan<byte> bytes) =>
        DecodeCatalog(bytes);

    async ValueTask<ulong> ReadNextWalSegmentIdAsync(CancellationToken cancellationToken)
    {
        foreach (var fileName in new[] { "manifest.snapshot.json", "manifest.json" })
        {
            _lease.EnsureValid();
            var remote = await _controlStore.GetAsync(
                PantsCloudObjectLayout.MetadataPrefix + fileName,
                cancellationToken).ConfigureAwait(false);
            if (remote is null)
            {
                continue;
            }

            return CloudManifestReader.DecodeManifest(remote.Data.Span).WalHistoryStart;
        }

        return 1;
    }

    static ProviderWalCatalog DecodeCatalog(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var catalog = JsonSerializer.Deserialize<ProviderWalCatalog>(bytes, JsonOptions) ??
                          throw new JsonException("Catalog is empty.");
            if (catalog.FormatVersion != 1)
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

            ulong? previousSegmentId = null;
            foreach (var (segmentId, segment) in catalog.Segments)
            {
                ValidateSegment(segmentId, segment, catalog.FencingEpoch);
                if (previousSegmentId.HasValue &&
                    segmentId != checked(previousSegmentId.Value + 1) &&
                    segmentId > catalog.WalHistoryStartSegmentId)
                {
                    throw new PantsCorruptionException(
                        "Cloud WAL catalog entries are not a contiguous history suffix.");
                }

                previousSegmentId = segmentId;
            }

            if (catalog.Segments.Count > 0)
            {
                var inferredNextSegmentId = checked(catalog.Segments.Keys.Max() + 1);
                if (catalog.NextSegmentId != 0 &&
                    (catalog.NextSegmentId < inferredNextSegmentId ||
                     catalog.NextSegmentId > inferredNextSegmentId &&
                     catalog.NextSegmentId > catalog.WalHistoryStartSegmentId))
                {
                    throw new PantsCorruptionException(
                        "Cloud WAL catalog next segment identity precedes its published suffix.");
                }

                if (catalog.NextSegmentId == 0)
                {
                    catalog = catalog with { NextSegmentId = inferredNextSegmentId };
                }
            }

            return catalog;
        }
        catch (JsonException exception)
        {
            throw new PantsCorruptionException("Cloud WAL catalog is malformed.", exception);
        }
    }

    static void ValidateSegment(
        ulong segmentId,
        ProviderPublishedWalSegment segment,
        ulong fencingEpoch)
    {
        if (segment.WriterEpoch == 0 ||
            segment.SegmentId != segmentId || segment.WriterEpoch > fencingEpoch ||
            segment.SizeBytes == 0 || segment.ObjectKey != PantsCloudObjectLayout.WalSegmentObjectKey(
                segment.WriterEpoch,
                segmentId))
        {
            throw new PantsCorruptionException($"Cloud WAL catalog entry {segmentId} is invalid.");
        }
    }
}
