using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Pants.Cloud.Internal;

sealed class ProviderCloudPersistence : ICloudPersistence
{
    /// <summary>
    ///     How much of a published WAL segment recovery holds in memory at once.
    /// </summary>
    public const int WalRecoveryPageBytes = 4 * 1024 * 1024;
    public const int WalPruningPageBytes = 256 * 1024;
    public const ulong MaximumWholeObjectWalRecoveryBytes = 256UL * 1024 * 1024;

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
    readonly ProviderSstPublisher _sstPublisher;
    readonly ulong _writerEpoch;
    int _disposed;
    int _persistenceAnomaly;

    public ProviderCloudPersistence(
        string localRoot,
        ICloudObjectStore walStore,
        ICloudObjectStore sstStore,
        ICloudObjectStore controlStore,
        CloudLeaseCoordinator lease,
        IFailpointHandler? failpoints = null,
        SstPublicationAdmission? sstPublicationAdmission = null)
    {
        _localRoot = Path.GetFullPath(localRoot);
        _walStore = walStore;
        _catalog = new ProviderWalCatalogStore(
            walStore,
            static bytes => DecodeCatalog(bytes.Span),
            lease.EnsureValid);
        _sstStore = sstStore;
        _sstPublisher = new ProviderSstPublisher(
            sstStore,
            lease.EnsureValid,
            sstPublicationAdmission);
        _controlStore = controlStore;
        _lease = lease;
        _writerEpoch = lease.Epoch;
        _sstGarbageCollector = new CloudSstGarbageCollector(
            _sstStore,
            CaptureSstRetentionProofAsync,
            () => CloudSstReferenceReader.ReadLocalProtectedNames(_localRoot),
            _lease.EnsureValid,
            failpoints ?? NullPantsFailpointHandler.Instance);
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

        // One remote check per batch, before any upload or catalog publication: the catalog CAS
        // only notices a successor that already fenced it, whereas this catches a successor that
        // has taken the lease object but not yet written the catalog.
        await _lease.ValidateRemoteAsync(cancellationToken).ConfigureAwait(false);
        var batchEpoch = segments[0].WriterEpoch;
        if (segments.Any(segment => segment.WriterEpoch != batchEpoch))
        {
            throw PantsException.InvalidArgument("A cloud WAL batch cannot cross writer epochs.");
        }

        if (batchEpoch > _writerEpoch)
        {
            throw new PantsFencedException("A WAL segment from a future cloud lease cannot be published.");
        }

        var publications = new ProviderPublishedWalSegment[segments.Count];
        for (var index = 0; index < segments.Count; index++)
        {
            _lease.EnsureValid();
            var segment = segments[index];
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

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var read = await _catalog.ReadAsync(false, cancellationToken).ConfigureAwait(false);
            _lease.EnsureValid();
            var current = read.Object;
            var catalog = read.Catalog ?? new ProviderWalCatalog { FencingEpoch = _writerEpoch };
            if (catalog.FencingEpoch != _writerEpoch)
            {
                throw new PantsFencedException("The cloud WAL catalog is not fenced to this writer.");
            }

            var updatedSegments = new SortedDictionary<ulong, ProviderPublishedWalSegment>(catalog.Segments);
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

                updatedSegments.Add(publication.SegmentId, publication);
                changed = true;
            }

            if (!changed)
            {
                _lease.EnsureValid();
                return;
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                catalog with { FencingEpoch = _writerEpoch, Segments = updatedSegments },
                JsonOptions);
            var published = await _walStore.PutAsync(
                PantsCloudObjectLayout.WalCatalogObjectKey,
                bytes,
                current is null
                    ? new PantsCloudObjectWriteCondition.IfAbsent()
                    : new PantsCloudObjectWriteCondition.IfVersion(current.Version),
                cancellationToken).ConfigureAwait(false);
            if (published)
            {
                var readback = await _walStore.GetAsync(
                    PantsCloudObjectLayout.WalCatalogObjectKey,
                    cancellationToken).ConfigureAwait(false);
                if (readback is not null && readback.Data.Span.SequenceEqual(bytes))
                {
                    await _catalog.ConvergeMirrorAsync(bytes, cancellationToken)
                        .ConfigureAwait(false);
                    _lease.EnsureValid();
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
        var metadata = CloudControlMetadataSnapshot.Capture(_localRoot, MetadataFiles);
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

        await PruneCoveredWalAsync(metadata, cancellationToken).ConfigureAwait(false);
    }

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
        var metadata = CloudControlMetadataSnapshot.Capture(_localRoot, ManifestMetadataFiles);
        await EnsureRemoteManifestNotAheadAsync(metadata, cancellationToken).ConfigureAwait(false);
        _lease.EnsureValid();
    }

    public async ValueTask<CloudDdlRegistryObject?> ReadDdlRegistryAsync(
        CancellationToken cancellationToken)
    {
        _lease.EnsureValid();
        var current = await _controlStore.GetAsync(
            PantsCloudObjectLayout.DdlRegistryObjectKey,
            cancellationToken).ConfigureAwait(false);
        _lease.EnsureValid();
        return current is null
            ? null
            : new CloudDdlRegistryObject(
                CloudDdlJson.DeserializeRegistry(current.Data.Span),
                current.Version);
    }

    public async ValueTask FenceDdlRegistryAsync(
        CloudDdlRegistry bootstrap,
        CancellationToken cancellationToken)
    {
        _lease.EnsureValid();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var current = await _controlStore.GetAsync(
                PantsCloudObjectLayout.DdlRegistryObjectKey,
                cancellationToken).ConfigureAwait(false);
            _lease.EnsureValid();
            var baseline = current?.Data.ToArray() ?? CloudDdlJson.SerializeRegistry(bootstrap);
            var fencedBytes = CloudDdlFence.Encode(baseline, _writerEpoch);
            if (current?.Data.Span.SequenceEqual(fencedBytes) == true)
            {
                return;
            }

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

            var readback = await _controlStore.GetAsync(
                               PantsCloudObjectLayout.DdlRegistryObjectKey,
                               cancellationToken).ConfigureAwait(false) ??
                           throw new PantsLeaseIndeterminateException(
                               "The cloud DDL registry fence was acknowledged without an authoritative object.");
            _lease.EnsureValid();
            if (!readback.Data.Span.SequenceEqual(fencedBytes))
            {
                throw new PantsCorruptionException(
                    "The cloud DDL registry fence read back different bytes.");
            }

            return;
        }

        throw new PantsBusyException(
            "Cloud DDL registry fencing exceeded its bounded CAS retries.");
    }

    public async ValueTask<bool> CompareExchangeDdlRegistryAsync(
        CloudDdlRegistry registry,
        string? expectedVersion,
        CancellationToken cancellationToken)
    {
        _lease.EnsureValid();
        var data = CloudDdlJson.SerializeRegistry(registry);
        var published = await _controlStore.PutAsync(
            PantsCloudObjectLayout.DdlRegistryObjectKey,
            data,
            expectedVersion is null
                ? new PantsCloudObjectWriteCondition.IfAbsent()
                : new PantsCloudObjectWriteCondition.IfVersion(expectedVersion),
            cancellationToken).ConfigureAwait(false);
        _lease.EnsureValid();
        if (!published)
        {
            return false;
        }

        var readback = await _controlStore.GetAsync(
                           PantsCloudObjectLayout.DdlRegistryObjectKey,
                           cancellationToken).ConfigureAwait(false) ??
                       throw new PantsLeaseIndeterminateException(
                           "The cloud DDL registry CAS was acknowledged without an authoritative object.");
        _lease.EnsureValid();
        if (!readback.Data.Span.SequenceEqual(data))
        {
            throw new PantsCorruptionException(
                "The cloud DDL registry CAS read back different bytes.");
        }

        return true;
    }

    public static async ValueTask<ProviderCloudHydrationResult> HydrateLocalCacheAsync(
        string localRoot,
        ICloudObjectStore walStore,
        ICloudObjectStore sstStore,
        ICloudObjectStore controlStore,
        PantsRecoveryPolicy recoveryPolicy,
        CancellationToken cancellationToken,
        ulong maximumWholeObjectBytes = MaximumWholeObjectWalRecoveryBytes)
    {
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
                value = await controlStore.GetAsync(
                    PantsCloudObjectLayout.MetadataPrefix + fileName,
                    cancellationToken).ConfigureAwait(false);
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
                AtomicStagedFile.Write(localPath, value.Data.Span);
            }
        }

        var activeManifest = useRemoteMetadata ? remoteManifest : localManifest ?? remoteManifest;

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

        var catalogRead = await new ProviderWalCatalogStore(
                walStore,
                static bytes => DecodeCatalog(bytes.Span),
                static () => { })
            .ReadAsync(true, cancellationToken).ConfigureAwait(false);
        if (catalogRead.Catalog is not { } catalog)
        {
            return new ProviderCloudHydrationResult(
                new Dictionary<ulong, ProviderPublishedWalSegment>(),
                0,
                metadataSalvage);
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

            AtomicStagedFile.Write(localPath, bytes.Span);
        }

        return new ProviderCloudHydrationResult(
            catalog.Segments,
            cloudDurableSequence,
            requiresSalvage,
            remoteSegments);
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

    public async ValueTask FenceWalCatalogAsync(CancellationToken cancellationToken)
    {
        _lease.EnsureValid();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var read = await _catalog.ReadAsync(false, cancellationToken).ConfigureAwait(false);
            var current = read.Object;
            var catalog = read.Catalog ?? new ProviderWalCatalog();
            if (catalog.FencingEpoch > _writerEpoch)
            {
                throw new PantsFencedException("The cloud WAL catalog has a newer fencing epoch.");
            }

            if (catalog.FencingEpoch == _writerEpoch)
            {
                return;
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                catalog with { FencingEpoch = _writerEpoch },
                JsonOptions);
            var fenced = await _walStore.PutAsync(
                PantsCloudObjectLayout.WalCatalogObjectKey,
                bytes,
                current is null
                    ? new PantsCloudObjectWriteCondition.IfAbsent()
                    : new PantsCloudObjectWriteCondition.IfVersion(current.Version),
                cancellationToken).ConfigureAwait(false);
            if (fenced)
            {
                var readback = await _walStore.GetAsync(
                                   PantsCloudObjectLayout.WalCatalogObjectKey,
                                   cancellationToken).ConfigureAwait(false) ??
                               throw new PantsLeaseIndeterminateException(
                                   "The cloud WAL catalog fence was acknowledged without an authoritative object.");
                if (!readback.Data.Span.SequenceEqual(bytes))
                {
                    throw new PantsCorruptionException(
                        "The cloud WAL catalog fence read back different bytes after CAS.");
                }

                await _catalog.ConvergeMirrorAsync(bytes, cancellationToken)
                    .ConfigureAwait(false);
                return;
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
        var protectedNames = new HashSet<string>(
            CloudSstReferenceReader.ReadLocalProtectedNames(_localRoot),
            StringComparer.Ordinal);
        var guards = new List<CloudObjectIdentityGuard>(ManifestMetadataFiles.Length + 1);
        Dictionary<uint, ulong>? remoteNextSequences = null;
        foreach (var fileName in ManifestMetadataFiles)
        {
            _lease.EnsureValid();
            var objectKey = PantsCloudObjectLayout.MetadataPrefix + fileName;
            var remote = await _controlStore.GetAsync(objectKey, cancellationToken)
                .ConfigureAwait(false);
            if (remote is null)
            {
                continue;
            }

            var manifest = CloudManifestReader.DecodeManifest(remote.Data.Span);
            CloudSstReferenceReader.AddManifestNames(manifest, protectedNames);
            remoteNextSequences = IntersectNextSstSequences(
                remoteNextSequences,
                manifest.NextSstSeqs);
            guards.Add(new CloudObjectIdentityGuard(
                _controlStore,
                objectKey,
                remote.Version));
        }

        var intentObjectKey = PantsCloudObjectLayout.MetadataPrefix + "intent_log.json";
        var remoteIntent = await _controlStore.GetAsync(intentObjectKey, cancellationToken)
            .ConfigureAwait(false);
        if (remoteIntent is not null)
        {
            CloudSstReferenceReader.AddIntentNames(remoteIntent.Data, protectedNames);
            guards.Add(new CloudObjectIdentityGuard(
                _controlStore,
                intentObjectKey,
                remoteIntent.Version));
        }

        return new CloudSstRetentionProof(
            protectedNames,
            remoteNextSequences ?? new Dictionary<uint, ulong>(),
            guards);
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

    async ValueTask PruneCoveredWalAsync(
        CloudControlMetadataSnapshot metadata,
        CancellationToken cancellationToken)
    {
        var manifest = metadata.AuthoritativeManifest;
        if (manifest is null)
        {
            return;
        }

        var coveredSequence = manifest.LastPersistedSequence;
        if (coveredSequence == 0)
        {
            return;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            _lease.EnsureValid();
            var read = await _catalog.ReadAsync(false, cancellationToken).ConfigureAwait(false);
            if (read.Object is not { } current || read.Catalog is not { } catalog)
            {
                return;
            }

            if (catalog.FencingEpoch != _writerEpoch)
            {
                throw new PantsFencedException(
                    "The cloud WAL catalog is not fenced to this writer during pruning.");
            }

            // Retire only the contiguous oldest prefix: a gap must stay recoverable in order, so a
            // segment that is not yet covered ends the prefix however many newer ones are.
            var candidates = new List<ProviderPublishedWalSegment>();
            foreach (var segment in catalog.Segments.Values.OrderBy(static segment => segment.SegmentId))
            {
                if (segment.MaximumSequence > coveredSequence)
                {
                    break;
                }

                candidates.Add(segment);
            }

            if (candidates.Count == 0)
            {
                return;
            }

            var retired = new List<ProviderPublishedWalSegment>(candidates.Count);
            var walGuards = new Dictionary<ulong, CloudObjectIdentityGuard>();
            var coveringFiles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var segment in candidates)
            {
                string? version;
                try
                {
                    version = await ProveWalSegmentCoveredAsync(
                        segment,
                        manifest,
                        coveringFiles,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is PantsTimeoutException or PantsResourceLimitException or PantsBusyException)
                {
                    // Out of time or memory is not an error: authority is retained and the proof
                    // resumes on a later maintenance turn. What is already proven still retires.
                    break;
                }

                if (version is null)
                {
                    break;
                }

                retired.Add(segment);
                walGuards.Add(
                    segment.SegmentId,
                    new CloudObjectIdentityGuard(_walStore, segment.ObjectKey, version));
            }

            if (retired.Count == 0 || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var dependencyGuards = await ValidateManifestDependenciesAsync(
                (ManifestState)manifest,
                coveringFiles,
                metadata,
                cancellationToken).ConfigureAwait(false);
            await VerifyIdentityGuardsAsync(
                dependencyGuards.Concat(walGuards.Values),
                cancellationToken).ConfigureAwait(false);
            _lease.EnsureValid();

            var retiredIds = retired
                .Select(static segment => segment.SegmentId)
                .ToHashSet();
            var retained = new SortedDictionary<ulong, ProviderPublishedWalSegment>(
                catalog.Segments
                    .Where(entry => !retiredIds.Contains(entry.Key))
                    .ToDictionary());
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                catalog with { Segments = retained },
                JsonOptions);
            var published = await _walStore.PutAsync(
                PantsCloudObjectLayout.WalCatalogObjectKey,
                bytes,
                new PantsCloudObjectWriteCondition.IfVersion(current.Version),
                cancellationToken).ConfigureAwait(false);
            if (!published)
            {
                continue;
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

            foreach (var segment in retired)
            {
                await TryDeleteRetiredWalAsync(
                    segment,
                    walGuards[segment.SegmentId].Version,
                    cancellationToken).ConfigureAwait(false);
            }

            return;
        }
    }

    async ValueTask TryDeleteRetiredWalAsync(
        ProviderPublishedWalSegment segment,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            _lease.EnsureValid();
            var outcome = await _walStore.DeleteAsync(
                segment.ObjectKey,
                new PantsCloudObjectDeleteCondition.IfVersion(expectedVersion),
                cancellationToken).ConfigureAwait(false);
            if (outcome != CloudObjectDeleteOutcome.Deleted)
            {
                Volatile.Write(ref _persistenceAnomaly, 1);
            }
        }
        catch (PantsException) when (!cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
            // The retired catalog is authoritative. Retaining an unproven
            // object as harmless residue is safer than an unconditional retry.
        }
    }

    /// <summary>
    ///     Proves a published WAL segment's size, checksum and manifest coverage by reading it a
    ///     bounded page at a time, so pruning memory does not depend on segment size.
    /// </summary>
    /// <returns>The object version that was proven, or null when the segment is not yet covered.</returns>
    async ValueTask<string?> ProveWalSegmentCoveredAsync(
        ProviderPublishedWalSegment segment,
        ManifestState manifest,
        HashSet<string> coveringFiles,
        CancellationToken cancellationToken)
    {
        _lease.EnsureValid();
        var metadata = await _walStore.HeadAsync(segment.ObjectKey, cancellationToken)
            .ConfigureAwait(false) ?? throw new PantsRecoveryFailedException(
            $"Published cloud WAL object '{segment.ObjectKey}' is missing during pruning.");
        if (metadata.SizeBytes != segment.SizeBytes)
        {
            throw new PantsCorruptionException(
                $"Published cloud WAL object '{segment.ObjectKey}' differs from its catalog proof.");
        }

        var covered = new HashSet<string>(StringComparer.Ordinal);
        bool isCovered;
        if (await TryVerifySegmentInPagesAsync(
                _walStore,
                segment,
                cancellationToken,
                WalPruningPageBytes).ConfigureAwait(false))
        {
            using var stream = new RemoteRangeStream(
                _walStore,
                segment.ObjectKey,
                checked((long)segment.SizeBytes),
                WalPruningPageBytes);
            isCovered = CloudWalCoverageValidator.ValidateStreamAndCollectCoveringFiles(
                stream,
                segment.MaximumSequence,
                segment.WriterEpoch,
                manifest,
                covered);
        }
        else
        {
            // No ranged reads (or a mismatch to classify): the whole object is the only proof, so
            // admit it against the whole-object limit before any GET.
            if (segment.SizeBytes > MaximumWholeObjectWalRecoveryBytes)
            {
                throw new PantsResourceLimitException(
                    $"Published cloud WAL object '{segment.ObjectKey}' is too large to prove " +
                    "without ranged reads.");
            }

            var remote = await _walStore.GetAsync(segment.ObjectKey, cancellationToken)
                .ConfigureAwait(false) ?? throw new PantsRecoveryFailedException(
                $"Published cloud WAL object '{segment.ObjectKey}' is missing during pruning.");
            if (checked((ulong)remote.Data.Length) != segment.SizeBytes ||
                DiskFormat.Crc32C(remote.Data.Span) != segment.ContentCrc32C)
            {
                throw new PantsCorruptionException(
                    $"Published cloud WAL object '{segment.ObjectKey}' differs from its catalog proof.");
            }

            using var stream = new MemoryStream(remote.Data.ToArray(), false);
            isCovered = CloudWalCoverageValidator.ValidateStreamAndCollectCoveringFiles(
                stream,
                segment.MaximumSequence,
                segment.WriterEpoch,
                manifest,
                covered);
        }

        _lease.EnsureValid();
        if (!isCovered)
        {
            return null;
        }

        coveringFiles.UnionWith(covered);
        return metadata.Version;
    }

    async ValueTask<IReadOnlyList<CloudObjectIdentityGuard>> ValidateManifestDependenciesAsync(
        ManifestState manifest,
        HashSet<string> coveringFileNames,
        CloudControlMetadataSnapshot metadata,
        CancellationToken cancellationToken)
    {
        var guards = new List<CloudObjectIdentityGuard>(
            coveringFileNames.Count + MetadataFiles.Length);

        // Only the SSTs that cover the retiring segments matter; unrelated ones are never touched.
        // An SST's bytes were verified when it was published and it is immutable, so existence,
        // length and an identity guard are the proof that it is still the covering object.
        foreach (var file in manifest.Files.Where(file => coveringFileNames.Contains(file.Name)))
        {
            _lease.EnsureValid();
            var objectKey = PantsCloudObjectLayout.SstPrefix + file.Name;
            var remote = await _sstStore.HeadAsync(objectKey, cancellationToken)
                .ConfigureAwait(false) ?? throw new PantsRecoveryFailedException(
                $"Manifest cloud SST '{file.Name}' is missing during WAL pruning.");
            _lease.EnsureValid();
            if (file.SizeBytes != 0 && remote.SizeBytes != file.SizeBytes)
            {
                throw new PantsCorruptionException(
                    $"Manifest cloud SST '{file.Name}' length differs during WAL pruning.");
            }

            guards.Add(new CloudObjectIdentityGuard(_sstStore, objectKey, remote.Version));
        }

        foreach (var (fileName, capturedBytes) in metadata.Files)
        {
            _lease.EnsureValid();
            var objectKey = PantsCloudObjectLayout.MetadataPrefix + fileName;
            var remote = await _controlStore.GetAsync(objectKey, cancellationToken)
                .ConfigureAwait(false) ?? throw new PantsRecoveryFailedException(
                $"Cloud metadata object '{objectKey}' is missing during WAL pruning.");
            _lease.EnsureValid();
            if (!remote.Data.Span.SequenceEqual(capturedBytes.Span))
            {
                throw new PantsCorruptionException(
                    $"Cloud metadata object '{objectKey}' differs from the published snapshot bytes.");
            }

            guards.Add(new CloudObjectIdentityGuard(_controlStore, objectKey, remote.Version));
        }

        return guards;
    }

    async ValueTask VerifyIdentityGuardsAsync(
        IEnumerable<CloudObjectIdentityGuard> guards,
        CancellationToken cancellationToken)
    {
        foreach (var guard in guards)
        {
            _lease.EnsureValid();
            var current = await guard.Store.HeadAsync(guard.ObjectKey, cancellationToken)
                .ConfigureAwait(false);
            _lease.EnsureValid();
            if (current is null ||
                !StringComparer.Ordinal.Equals(current.Version, guard.Version))
            {
                throw new PantsLeaseIndeterminateException(
                    $"Cloud WAL pruning dependency '{guard.ObjectKey}' changed after validation.");
            }
        }
    }

    internal static ProviderWalCatalog DecodeCatalogForFloor(ReadOnlySpan<byte> bytes) =>
        DecodeCatalog(bytes);

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

            foreach (var (segmentId, segment) in catalog.Segments)
            {
                ValidateSegment(segmentId, segment, catalog.FencingEpoch);
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
