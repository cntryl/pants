using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cntryl.Pants.Cloud.Internal.WalRetirement;
using Cntryl.Pants.Storage.Internal;
using Cntryl.Pants.Storage.Internal.IO;

namespace Cntryl.Pants.Cloud.Internal;

sealed class SimulatedCloudPersistence : ICloudPersistence
{
    const uint CatalogFormatVersion = 3;

    static readonly string[] MetadataFiles =
    [
        "FORMAT",
        "manifest.snapshot.json",
        "manifest.journal",
        "intent_log.json"
    ];

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    readonly string _cloudRoot;
    readonly IFailpointHandler _failpoints;
    readonly SstPublicationAdmission _sstPublicationAdmission;

    readonly string _localRoot;
    readonly SimulatedCloudObjectStore _objectStore;
    readonly CloudMetadataAuthority _metadataAuthority;
    readonly CloudObjectLeaseStore _simulatedLeaseStore;
    readonly CloudLeaseCoordinator _lease;
    readonly bool _ownsLease;
    readonly ResourceBudget _metadataSnapshotBudget = new(64L * 1024 * 1024);
    readonly ulong _writerEpoch;

    WalPublicationCatalog _catalog;
    string? _catalogPrimaryVersion;
    string? _catalogMirrorVersion;
    bool _catalogMirrorPending;
    int _persistenceAnomaly;
    int _disposed;
    Func<IReadOnlyDictionary<string, FileMeta>>? _readSstDeletionCandidates;
    Action<FileMeta>? _acknowledgeSstDeletion;
    int _sstDeletionAcknowledgements;

    public SimulatedCloudPersistence(
        string localRoot,
        ulong writerEpoch,
        IFailpointHandler? failpoints = null,
        SstPublicationAdmission? sstPublicationAdmission = null,
        CloudLeaseCoordinator? lease = null)
    {
        if (writerEpoch == 0)
        {
            throw new PantsInvalidArgumentException("Simulated cloud requires a nonzero writer epoch.");
        }

        _localRoot = Path.GetFullPath(localRoot);
        _cloudRoot = Path.Combine(_localRoot, "cloud_store");
        _writerEpoch = writerEpoch;
        _failpoints = failpoints ?? NullPantsFailpointHandler.Instance;
        _sstPublicationAdmission = sstPublicationAdmission ?? SstPublicationAdmission.Gated(
            new ResourceBudget(64L * 1024 * 1024),
            new MaintenanceMemoryGate());
        Directory.CreateDirectory(_cloudRoot);
        _catalog = LoadCatalog(_cloudRoot, out _catalogPrimaryVersion, out _catalogMirrorVersion) ?? WalPublicationCatalog.Empty(writerEpoch);
        if (_catalog.NextSegmentId == 0)
        {
            _catalog.NextSegmentId = ReadNextWalSegmentId(_cloudRoot);
        }

        if (_catalog.FencingEpoch > writerEpoch)
        {
            throw PantsException.Create(
                PantsErrorCode.Fenced,
                $"The cloud WAL catalog is fenced at epoch {_catalog.FencingEpoch}.");
        }

        _objectStore = new SimulatedCloudObjectStore(_cloudRoot);
        _simulatedLeaseStore = new CloudObjectLeaseStore(_objectStore, PantsCloudObjectLayout.LeaseObjectKey);
        _ownsLease = lease is null;
        _lease = lease ?? new CloudLeaseCoordinator(
            _simulatedLeaseStore,
            new NonDecreasingPantsClock(SystemPantsClock.Instance),
            $"pants-simulated-{Environment.ProcessId}-{Guid.NewGuid():N}",
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(15));
        CloudMetadataAuthority? metadataAuthority = null;
        try
        {
            if (_ownsLease)
            {
                _ = _lease.AcquireAsync(writerEpoch == 0 ? 0 : writerEpoch - 1, CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
            }

            if (_lease.Epoch != writerEpoch)
            {
                throw new PantsFencedException("Simulated cloud lease and writer epochs must match.");
            }

            _lease.ValidateRemoteAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            _catalog.FencingEpoch = writerEpoch;
            SaveCatalog();
            _metadataAuthority = metadataAuthority = new CloudMetadataAuthority(_objectStore, _lease, _metadataSnapshotBudget);
            _metadataAuthority.ClaimAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            MirrorMetadataAndSsts();
        }
        catch
        {
            metadataAuthority?.Dispose();
            if (_ownsLease)
            {
                try
                {
                    _lease.ReleaseAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
                }
                finally
                {
                    _lease.Dispose();
                }
            }

            throw;
        }
    }

    internal static ulong ReadStartupFencingEpoch(string localRoot) =>
        LoadCatalog(Path.Combine(Path.GetFullPath(localRoot), "cloud_store"), repairCopies: false)?.FencingEpoch ?? 0;

    internal void BindSstDeletionAuthority(Func<IReadOnlyDictionary<string, FileMeta>> candidates, Action<FileMeta> acknowledge)
    {
        _readSstDeletionCandidates = candidates;
        _acknowledgeSstDeletion = file =>
        {
            acknowledge(file);
            Interlocked.Increment(ref _sstDeletionAcknowledgements);
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _metadataAuthority.Dispose();
        if (_ownsLease)
        {
            try
            {
                await _lease.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _lease.Dispose();
            }
        }

        GC.SuppressFinalize(this);
    }

    public bool HasPersistenceAnomaly => Volatile.Read(ref _persistenceAnomaly) != 0;

    public void EnsureReadAuthority() => EnsureSimulatedAuthority();

    public ulong WriterEpoch => _writerEpoch;

    public ValueTask<CloudDdlRegistryObject?> ReadDdlRegistryAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveObjectPath(_cloudRoot, PantsCloudObjectLayout.DdlRegistryObjectKey);
        if (!File.Exists(path))
        {
            return ValueTask.FromResult<CloudDdlRegistryObject?>(null);
        }

        var bytes = File.ReadAllBytes(path);
        return ValueTask.FromResult<CloudDdlRegistryObject?>(new CloudDdlRegistryObject(
            CloudDdlJson.DeserializeRegistry(bytes),
            CreateVersion(bytes),
            bytes));
    }

    public ValueTask FenceDdlRegistryAsync(
        CloudDdlRegistry bootstrap,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveObjectPath(_cloudRoot, PantsCloudObjectLayout.DdlRegistryObjectKey);
        var current = File.Exists(path)
            ? File.ReadAllBytes(path)
            : CloudDdlJson.SerializeRegistry(bootstrap);
        var fenced = CloudDdlFence.Encode(current, _writerEpoch);
        if (!File.Exists(path) || !current.AsSpan().SequenceEqual(fenced))
        {
            AtomicStagedFile.Write(path, fenced);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> CompareExchangeDdlRegistryAsync(
        CloudDdlRegistry registry,
        string? expectedVersion,
        CancellationToken cancellationToken,
        Action? beforeSubmission = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveObjectPath(_cloudRoot, PantsCloudObjectLayout.DdlRegistryObjectKey);
        var exists = File.Exists(path);
        if (expectedVersion is null)
        {
            if (exists)
            {
                return ValueTask.FromResult(false);
            }
        }
        else if (!exists || !StringComparer.Ordinal.Equals(
                     CreateFileVersion(path),
                     expectedVersion))
        {
            return ValueTask.FromResult(false);
        }

        registry.WriterEpoch = _writerEpoch;
        var bytes = CloudDdlJson.SerializeRegistry(registry);
        beforeSubmission?.Invoke();
        AtomicStagedFile.Write(path, bytes);
        return ValueTask.FromResult(true);
    }

    public ValueTask PublishWalBatchAsync(
        IReadOnlyList<SealedWalSegment> segments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PublishWalBatchCatalog(segments);
        return ValueTask.CompletedTask;
    }

    public async ValueTask MirrorMetadataAndSstsAsync(CancellationToken cancellationToken)
    {
        using var receipt = await MirrorMetadataAndSstsWithReceiptAsync(0, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<CloudMetadataPublicationReceipt> MirrorMetadataAndSstsWithReceiptAsync(ulong accountingOwnerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return MirrorMetadataAndSstsCoreAsync(accountingOwnerId, cancellationToken);
    }

    public ValueTask ReconcileRecoveredMetadataAsync(CancellationToken cancellationToken) =>
        MirrorMetadataAndSstsAsync(cancellationToken);

    public async ValueTask CollectObsoleteSstsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var acknowledged = Volatile.Read(ref _sstDeletionAcknowledgements);
        CollectObsoleteSsts();
        await PublishDeletionAcknowledgementsAsync(acknowledged, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     The simulated store is a local directory with no provider latency, so it still retires
    ///     covered WAL inline at the end of each mirror; there is no background work to run.
    /// </summary>
    public ValueTask<WalRetirementResult> RetireCoveredWalAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(WalRetirementResult.Settled);
    }

    void EnsureSimulatedAuthority()
    {
        _lease.EnsureValid();
        var current = LoadCatalog(_cloudRoot, repairCopies: false);
        var lease = _simulatedLeaseStore.ReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        if (current is null || current.FencingEpoch != _writerEpoch || lease?.Lease.Epoch != _writerEpoch || lease.Lease.HolderId != _lease.HolderId || lease.Lease.OwnerToken != _lease.OwnerToken)
        {
            throw new PantsFencedException("Simulated cloud authority belongs to another writer epoch.");
        }
    }

    public ValueTask ValidateWriteAuthorityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSimulatedAuthority();
        return ValueTask.CompletedTask;
    }

    public static SimulatedCloudHydrationResult PrepareLocalCache(
        string localRoot,
        PantsRecoveryPolicy recoveryPolicy)
    {
        var root = Path.GetFullPath(localRoot);
        StoragePathGuard.EnsureTreeHasNoLinks(root);
        var cloudRoot = Path.Combine(root, "cloud_store");
        if (!Directory.Exists(cloudRoot))
        {
            return new SimulatedCloudHydrationResult(
                0,
                false,
                1);
        }

        var localManifest = CloudManifestReader.ReadManifest(root);
        var objectStore = new SimulatedCloudObjectStore(cloudRoot);
        _ = new CloudObjectLeaseStore(objectStore, PantsCloudObjectLayout.LeaseObjectKey).ReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var catalog = LoadCatalog(cloudRoot, repairCopies: false);
        var selected = CloudMetadataAuthority.ReadCommittedFilesAsync(objectStore, CancellationToken.None,
                minimumGeneration: catalog?.RetiredBy ?? 0)
            .AsTask().GetAwaiter().GetResult();
        if (selected is { Count: 0 })
        {
            CloudInitializationCacheValidator.Validate(root);
            selected = null;
        }

        var remoteManifest = selected is not null && selected.ContainsKey("manifest.snapshot.json") ? CloudManifestReader.DecodeMetadata(selected) : null;
        if (selected is not null)
        {
            foreach (var fileName in MetadataFiles)
            {
                var localPath = Path.Combine(root, fileName);
                if (selected.TryGetValue(fileName, out var body))
                {
                    AtomicStagedFile.Write(localPath, body.Span);
                }
                else if (File.Exists(localPath))
                {
                    File.Delete(localPath);
                }
            }
        }

        var activeManifest = remoteManifest ?? localManifest;
        var remoteNextWalSegmentId = activeManifest?.WalHistoryStart ?? 1;

        var localSstDirectory = Path.Combine(root, "sst");
        Directory.CreateDirectory(localSstDirectory);
        foreach (var file in activeManifest?.Files ?? [])
        {
            var localPath = Path.Combine(localSstDirectory, file.Name);
            var cloudPath = Path.Combine(cloudRoot, "sst", file.Name);
            if (!File.Exists(cloudPath))
            {
                var isRemoteAuthoritative = localManifest is null || remoteManifest?.Files.Any(remoteFile =>
                    StringComparer.Ordinal.Equals(
                        remoteFile.Name,
                        file.Name)) == true;
                if (isRemoteAuthoritative || !File.Exists(localPath))
                {
                    throw new PantsRecoveryFailedException(
                        $"Authoritative simulated-cloud SST '{file.Name}' is missing.");
                }

                continue;
            }

            var remoteLength = new FileInfo(cloudPath).Length;
            if (file.SizeBytes != 0 && checked((ulong)remoteLength) != file.SizeBytes)
            {
                throw new PantsCorruptionException(
                    $"Simulated-cloud SST '{file.Name}' length differs from its manifest.");
            }
        }

        if (catalog is null)
        {
            return new SimulatedCloudHydrationResult(0, false, remoteNextWalSegmentId);
        }

        var localWalDirectory = Path.Combine(root, "wal");
        Directory.CreateDirectory(localWalDirectory);
        var requiresSalvage = false;
        var cloudDurableSequence = catalog.Anchor.LastSeq;
        foreach (var (segmentId, publication) in catalog.Segments)
        {
            ValidatePublication(segmentId, publication, catalog.FencingEpoch);
            var remotePath = ResolveObjectPath(cloudRoot, publication.ObjectKey);
            if (!File.Exists(remotePath))
            {
                throw PantsException.Create(
                    PantsErrorCode.RecoveryFailed,
                    $"Published cloud WAL object '{publication.ObjectKey}' is missing.");
            }

            var bytes = File.ReadAllBytes(remotePath);
            if (checked((ulong)bytes.Length) != publication.SizeBytes ||
                DiskFormat.Crc32C(bytes) != publication.ContentCrc32C)
            {
                if (recoveryPolicy == PantsRecoveryPolicy.Strict)
                {
                    throw new PantsRecoveryFailedException(
                        $"Published cloud WAL object '{publication.ObjectKey}' failed catalog validation.");
                }

                requiresSalvage = true;
                bytes = CloudWalSalvage.CreateLocalRecoveryBytes(bytes).ToArray();
            }
            else if (!requiresSalvage)
            {
                cloudDurableSequence = Math.Max(cloudDurableSequence, publication.MaximumSequence);
            }

            var localName = $"{segmentId:00000000000000000000}.wal";
            AtomicStagedFile.Write(Path.Combine(localWalDirectory, localName), bytes);
        }

        return new SimulatedCloudHydrationResult(
            catalog.FencingEpoch,
            requiresSalvage,
            catalog.NextSegmentId == 0 ? remoteNextWalSegmentId : catalog.NextSegmentId,
            catalog.Segments.Count == 0 ? catalog.Anchor : catalog.Segments.Values.Last().Facts!.Link,
            catalog.SequenceFloor,
            catalog.Segments.Keys.Select(id => (IRemoteWalSegment)new DownloadedWalSegment(id,
                Path.Combine(localWalDirectory, $"{id:00000000000000000000}.wal"))).ToArray(),
            cloudDurableSequence);
    }

    public void PublishWal(SealedWalSegment segment) => PublishWalBatch([segment]);

    public void PublishWalBatch(IReadOnlyList<SealedWalSegment> segments)
    {
        PublishWalBatchCatalog(segments);
    }

    bool PublishWalBatchCatalog(IReadOnlyList<SealedWalSegment> segments)
    {
        if (segments.Count == 0)
        {
            return false;
        }

        EnsureSimulatedAuthority();
        var batchEpoch = segments[0].WriterEpoch;
        if (segments.Any(segment => segment.WriterEpoch != batchEpoch))
        {
            throw PantsException.InvalidArgument("A cloud WAL batch cannot cross writer epochs.");
        }

        if (batchEpoch > _writerEpoch)
        {
            throw PantsException.Create(
                PantsErrorCode.Fenced,
                "A WAL segment from a future writer epoch cannot be published.");
        }

        var nextSegmentId = _catalog.NextSegmentId;
        ulong historyStartSegmentId;
        using (var metadata = CloudControlMetadataSnapshot.Capture(
                   _localRoot,
                   MetadataFiles,
                   _metadataSnapshotBudget))
        {
            historyStartSegmentId = metadata.AuthoritativeManifest?.WalHistoryStart ?? 1;
        }
        var publishableSegments = new List<SealedWalSegment>(segments.Count);
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (index > 0 && segment.SegmentId <= segments[index - 1].SegmentId)
            {
                throw new PantsCorruptionException(
                    "A simulated-cloud WAL publication batch must contain consecutive segment identities.");
            }

            if (_catalog.Segments.TryGetValue(segment.SegmentId, out var published))
            {
                var candidate = new PublishedWalSegment
                {
                    Previous = segment.Header.Prev,
                    Facts = segment.Facts,
                    SegmentId = segment.SegmentId,
                    WriterEpoch = segment.WriterEpoch,
                    MaximumSequence = segment.MaximumSequence,
                    SizeBytes = checked((ulong)segment.Bytes.Length),
                    ContentCrc32C = DiskFormat.Crc32C(segment.Bytes),
                    ObjectKey = PantsCloudObjectLayout.WalSegmentObjectKey(
                        segment.WriterEpoch,
                        segment.SegmentId)
                };
                if (!published.Equals(candidate))
                {
                    throw PantsException.Create(
                        PantsErrorCode.Fenced,
                        $"Cloud WAL segment {segment.SegmentId} conflicts with its publication catalog entry.");
                }

                continue;
            }

            if (segment.SegmentId < nextSegmentId)
            {
                // A durable catalog high-water mark survives retirement. A local segment
                // left behind by a crash after remote retirement is already authoritative.
                continue;
            }

            var predecessor = publishableSegments.Count > 0 ? publishableSegments[^1].Facts.Link :
                _catalog.Segments.Count > 0 ? _catalog.Segments.Values.Last().Facts!.Link : _catalog.Anchor;
            if (!segment.Header.Prev.Matches(predecessor))
            {
                throw new PantsCorruptionException("A simulated WAL publication does not link the catalog head.");
            }

            nextSegmentId = checked(segment.SegmentId + 1);
            publishableSegments.Add(segment);
        }

        if (publishableSegments.Count == 0)
        {
            if (_catalogMirrorPending)
            {
                SaveCatalog();
            }

            return false;
        }

        var publications = publishableSegments.Select(segment => new PublishedWalSegment
        {
            Previous = segment.Header.Prev,
            Facts = segment.Facts,
            SegmentId = segment.SegmentId,
            WriterEpoch = segment.WriterEpoch,
            MaximumSequence = segment.MaximumSequence,
            SizeBytes = checked((ulong)segment.Bytes.Length),
            ContentCrc32C = DiskFormat.Crc32C(segment.Bytes),
            ObjectKey = PantsCloudObjectLayout.WalSegmentObjectKey(
                segment.WriterEpoch,
                segment.SegmentId)
        }).ToArray();

        var changed = false;
        for (var index = 0; index < publications.Length; index++)
        {
            var publication = publications[index];
            if (_catalog.Segments.TryGetValue(publication.SegmentId, out var existing))
            {
                if (!existing.Equals(publication))
                {
                    throw PantsException.Create(
                        PantsErrorCode.Fenced,
                        $"Cloud WAL segment {publication.SegmentId} conflicts with its publication catalog entry.");
                }

                continue;
            }

            AtomicStagedFile.Write(
                ResolveObjectPath(_cloudRoot, publication.ObjectKey),
                publishableSegments[index].Bytes);
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        var updatedSegments = new SortedDictionary<ulong, PublishedWalSegment>(_catalog.Segments);
        foreach (var publication in publications)
        {
            updatedSegments[publication.SegmentId] = publication;
        }

        var updatedCatalog = new WalPublicationCatalog
        {
            Anchor = _catalog.Anchor,
            SequenceFloor = _catalog.SequenceFloor,
            RetiredBy = _catalog.RetiredBy,
            FormatVersion = _catalog.FormatVersion,
            FencingEpoch = _catalog.FencingEpoch,
            NextSegmentId = nextSegmentId,
            WalHistoryStartSegmentId = Math.Max(
                _catalog.WalHistoryStartSegmentId,
                historyStartSegmentId),
            Segments = updatedSegments
        };
        SaveCatalog(updatedCatalog);
        _catalog = updatedCatalog;
        return true;
    }

    public void MirrorMetadataAndSsts() =>
        MirrorMetadataAndSstsAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

    async ValueTask<CloudMetadataPublicationReceipt> MirrorMetadataAndSstsCoreAsync(ulong accountingOwnerId, CancellationToken cancellationToken)
    {
        var metadata = CloudControlMetadataSnapshot.Capture(
            _localRoot,
            MetadataFiles,
            _metadataSnapshotBudget);
        try
        {
            var localSstDirectory = Path.Combine(_localRoot, "sst");
            var pendingSstCount = 0L;
            foreach (var localSst in EnumerateLocalSsts())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = ValidateSstName(Path.GetFileName(localSst));
                var remotePath = Path.Combine(_cloudRoot, "sst", name);
                if (!File.Exists(remotePath))
                {
                    pendingSstCount = checked(pendingSstCount + 1);
                    continue;
                }

                var localLength = new FileInfo(localSst).Length;
                if (new FileInfo(remotePath).Length != localLength)
                {
                    throw new PantsFencedException(
                        $"Immutable simulated-cloud SST '{Path.GetFileName(localSst)}' conflicts.");
                }

                using var admission = await _sstPublicationAdmission.AdmitAsync(
                        localLength,
                        cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var localBytes = File.ReadAllBytes(localSst);
                var remoteBytes = File.ReadAllBytes(remotePath);
                if (!remoteBytes.AsSpan().SequenceEqual(localBytes))
                {
                    throw new PantsFencedException(
                        $"Immutable simulated-cloud SST '{Path.GetFileName(localSst)}' conflicts.");
                }
            }

            foreach (var references in metadata.ReferencedSsts
                         .GroupBy(static file => file.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = ValidateSstName(references.Key);
                var localPath = Path.Combine(localSstDirectory, name);
                var remotePath = Path.Combine(_cloudRoot, "sst", name);
                if (File.Exists(localPath) && !File.Exists(remotePath))
                {
                    using var admission = await _sstPublicationAdmission.AdmitAsync(
                            new FileInfo(localPath).Length,
                            cancellationToken)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytes = File.ReadAllBytes(localPath);
                    foreach (var proof in references)
                    {
                        CloudSstValidator.Validate(bytes, proof);
                    }
                }
            }

            if (pendingSstCount > 0)
            {
                _failpoints.Hit(Failpoint.BeforeCloudUpload);
                foreach (var localSst in EnumerateLocalSsts())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = ValidateSstName(Path.GetFileName(localSst));
                    var remotePath = Path.Combine(_cloudRoot, "sst", name);
                    if (File.Exists(remotePath))
                    {
                        continue;
                    }

                    using var admission = await _sstPublicationAdmission.AdmitAsync(
                            new FileInfo(localSst).Length,
                            cancellationToken)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var bytes = File.ReadAllBytes(localSst);
                    AtomicStagedFile.Write(remotePath, bytes);
                }

                _failpoints.Hit(Failpoint.AfterCloudUpload);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ValidateCapturedSstMetadata(metadata, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            await _metadataAuthority.PublishAsync(metadata, cancellationToken).ConfigureAwait(false);

            var acknowledged = Volatile.Read(ref _sstDeletionAcknowledgements);
            CollectObsoleteSsts();

            PruneCoveredWal(metadata);
            await PublishDeletionAcknowledgementsAsync(acknowledged, cancellationToken).ConfigureAwait(false);

            return new CloudMetadataPublicationReceipt(metadata, accountingOwnerId);

            IEnumerable<string> EnumerateLocalSsts() => Directory.Exists(localSstDirectory)
                ? Directory.EnumerateFiles(
                    localSstDirectory,
                    "*.sst",
                    SearchOption.TopDirectoryOnly)
                : Enumerable.Empty<string>();
        }
        catch
        {
            metadata.Dispose();
            throw;
        }
    }

    async ValueTask PublishDeletionAcknowledgementsAsync(int previous, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _sstDeletionAcknowledgements) != previous)
        {
            using var metadata = CloudControlMetadataSnapshot.Capture(_localRoot, MetadataFiles, _metadataSnapshotBudget);
            await _metadataAuthority.PublishAsync(metadata, cancellationToken).ConfigureAwait(false);
        }
    }

    void ValidateCapturedSstMetadata(
        CloudControlMetadataSnapshot metadata,
        CancellationToken cancellationToken)
    {
        foreach (var file in metadata.ReferencedSsts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = ValidateSstName(file.Name);
            var path = Path.Combine(_cloudRoot, "sst", name);
            if (!File.Exists(path))
            {
                throw new PantsRecoveryFailedException(
                    $"Manifest simulated-cloud SST '{name}' is unavailable for publication.");
            }

            var source = LocalAsyncSstSource.Open(path);
            var reader = AsyncSstReader.OpenAsync(source, file, cancellationToken)
                .AsTask().GetAwaiter().GetResult();
            reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    static string ValidateSstName(string name)
    {
        if (!CloudSstObjectKey.TryGetName(
                PantsCloudObjectLayout.SstPrefix + name,
                out var validatedName) ||
            !StringComparer.Ordinal.Equals(name, validatedName))
        {
            throw new PantsCorruptionException(
                $"Simulated-cloud SST name '{name}' is unsafe.");
        }

        return validatedName;
    }

    static void WriteIfChanged(string path, ReadOnlySpan<byte> bytes)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            return;
        }

        AtomicStagedFile.Write(path, bytes);
    }

    void CollectObsoleteSsts()
    {
        if (!SimulatedCloudSstGarbageCollector.Collect(
                _localRoot,
                _cloudRoot,
                _failpoints,
                _readSstDeletionCandidates,
                _acknowledgeSstDeletion,
                EnsureSimulatedAuthority))
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
        }
    }

    // Guard/probe reads never repair. Only lease-owned conditional publication may converge copies.
    static WalPublicationCatalog? LoadCatalog(string cloudRoot, bool repairCopies = false)
    {
        if (repairCopies)
        {
            throw new PantsInvalidArgumentException("Catalog repair requires lease-owned conditional publication.");
        }

        return LoadCatalog(cloudRoot, out _, out _);
    }

    static WalPublicationCatalog? LoadCatalog(
        string cloudRoot,
        out string? primaryVersion,
        out string? mirrorVersion)
    {
        var objects = new SimulatedCloudObjectStore(cloudRoot);
        using var reads = new ProviderCloudControlMetadataReadSet(objects, new ResourceBudget(64L * 1024 * 1024));
        var primaryObject = reads.ReadAsync(PantsCloudObjectLayout.WalCatalogObjectKey, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        var mirrorObject = reads.ReadAsync(PantsCloudObjectLayout.WalCatalogMirrorObjectKey, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        primaryVersion = primaryObject?.Version;
        mirrorVersion = mirrorObject?.Version;
        var primaryBytes = primaryObject?.Data.ToArray();
        var mirrorBytes = mirrorObject?.Data.ToArray();
        var primary = TryDecodeCatalog(primaryBytes, out var primaryError);
        if (primary is not null)
        {
            return primary;
        }

        var mirror = TryDecodeCatalog(mirrorBytes, out var mirrorError);
        if (mirror is not null)
        {
            return mirror;
        }

        if (primaryBytes is null && mirrorBytes is null)
        {
            var walDirectory = Path.Combine(cloudRoot, "wal");
            if (Directory.Exists(walDirectory) &&
                Directory.EnumerateFiles(walDirectory, "*.wal", SearchOption.AllDirectories).Any())
            {
                throw new PantsRecoveryFailedException(
                    $"Cloud WAL segment objects exist but the publication catalog " +
                    $"'{PantsCloudObjectLayout.WalCatalogObjectKey}' and its mirror are both missing; " +
                    "refusing to open and orphan acknowledged writes.");
            }

            return null;
        }

        var primaryFailure = primaryBytes is null ? "missing" : $"invalid: {primaryError?.Message}";
        var mirrorFailure = mirrorBytes is null ? "missing" : $"invalid: {mirrorError?.Message}";
        throw new PantsCorruptionException(
            "Both cloud WAL publication catalog copies are unusable " +
            $"(primary: {primaryFailure}; mirror: {mirrorFailure}).");
    }

    static ulong ReadNextWalSegmentId(string cloudRoot)
    {
        var nextSegmentId = 1UL;
        foreach (var fileName in new[] { "manifest.snapshot.json", "manifest.json" })
        {
            var path = Path.Combine(cloudRoot, "metadata", fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            var manifest = CloudManifestReader.DecodeManifest(File.ReadAllBytes(path));
            nextSegmentId = Math.Max(nextSegmentId, manifest.WalHistoryStart);
        }

        return nextSegmentId;
    }

    static WalPublicationCatalog? TryDecodeCatalog(byte[]? bytes, out PantsException? error)
    {
        error = null;
        if (bytes is null)
        {
            return null;
        }

        try
        {
            var decoded = ProviderCloudPersistence.DecodeCatalogForFloor(bytes);
            var catalog = new WalPublicationCatalog
            {
                FormatVersion = decoded.FormatVersion,
                FencingEpoch = decoded.FencingEpoch,
                Anchor = decoded.Anchor,
                RetiredBy = decoded.RetiredBy,
                SequenceFloor = decoded.SequenceFloor,
                NextSegmentId = decoded.NextSegmentId,
                WalHistoryStartSegmentId = decoded.WalHistoryStartSegmentId,
                Segments = new SortedDictionary<ulong, PublishedWalSegment>(decoded.Segments.ToDictionary(
                    static pair => pair.Key, static pair => new PublishedWalSegment
                    {
                        Previous = pair.Value.Previous,
                        Facts = pair.Value.Facts,
                        SegmentId = pair.Value.SegmentId,
                        WriterEpoch = pair.Value.WriterEpoch,
                        MaximumSequence = pair.Value.MaximumSequence,
                        SizeBytes = pair.Value.SizeBytes,
                        ContentCrc32C = pair.Value.ContentCrc32C,
                        ObjectKey = pair.Value.ObjectKey
                    }))
            };

            return catalog;
        }
        catch (JsonException exception)
        {
            error = PantsException.Create(
                PantsErrorCode.Corruption,
                "Cloud WAL publication catalog v3 cannot be decoded.",
                exception);
            return null;
        }
        catch (PantsCorruptionException exception)
        {
            error = exception;
            return null;
        }
    }

    static void ValidatePublication(
        ulong segmentId,
        PublishedWalSegment publication,
        ulong fencingEpoch)
    {
        if (segmentId == 0)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                $"Cloud WAL catalog entry {segmentId} is invalid.");
        }

        var expectedKey = PantsCloudObjectLayout.WalSegmentObjectKey(
            publication.WriterEpoch,
            segmentId);
        if (publication.SegmentId != segmentId ||
            publication.WriterEpoch > fencingEpoch ||
            publication.SizeBytes == 0 ||
            publication.ObjectKey != expectedKey)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                $"Cloud WAL catalog entry {segmentId} is invalid.");
        }
    }

    void SaveCatalog() => SaveCatalog(_catalog);

    void SaveCatalog(WalPublicationCatalog catalog)
    {
        var wire = new ProviderWalCatalog
        {
            FencingEpoch = catalog.FencingEpoch,
            Anchor = catalog.Anchor,
            RetiredBy = catalog.RetiredBy,
            SequenceFloor = catalog.SequenceFloor,
            NextSegmentId = catalog.NextSegmentId,
            Segments = new SortedDictionary<ulong, ProviderPublishedWalSegment>(catalog.Segments.ToDictionary(
                static pair => pair.Key, static pair => new ProviderPublishedWalSegment
                {
                    Previous = pair.Value.Previous,
                    Facts = pair.Value.Facts,
                    SegmentId = pair.Value.SegmentId,
                    WriterEpoch = pair.Value.WriterEpoch,
                    MaximumSequence = pair.Value.MaximumSequence,
                    SizeBytes = pair.Value.SizeBytes,
                    ContentCrc32C = pair.Value.ContentCrc32C,
                    ObjectKey = pair.Value.ObjectKey
                }))
        };
        using var serialized = new CloudMetadataWriteAdmission(_metadataSnapshotBudget).Serialize(wire, JsonOptions);
        var bytes = serialized.Bytes;
        _ = ProviderCloudPersistence.DecodeCatalogForFloor(bytes.Span);
        _lease.ValidateRemoteAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        _failpoints.Hit(Failpoint.BeforeSimulatedCloudCatalogWrite);
        bool published;
        try
        {
            published = _objectStore.PutAsync(PantsCloudObjectLayout.WalCatalogObjectKey, bytes,
                    _catalogPrimaryVersion is null
                        ? new CloudObjectWriteCondition.IfAbsent()
                        : new CloudObjectWriteCondition.IfVersion(_catalogPrimaryVersion),
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            throw CatalogPublicationUnknown(exception);
        }

        if (!published)
        {
            var current = _objectStore.HeadAsync(PantsCloudObjectLayout.WalCatalogObjectKey)
                .AsTask().GetAwaiter().GetResult();
            if (current?.Version != CreateVersion(bytes.Span) || current.SizeBytes != checked((ulong)bytes.Length))
            {
                throw new PantsFencedException("The simulated cloud WAL catalog changed after its publication snapshot was read.");
            }

            // A lost native write receipt can leave exactly this target committed. Its content
            // identity is sufficient for reconciliation only while the full lease remains ours.
            _lease.ValidateRemoteAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        // A known primary commit owns this exact head even if mirror convergence fails.
        _catalogPrimaryVersion = CreateVersion(bytes.Span);
        _catalog = catalog;
        _catalogMirrorPending = true;
        try
        {
            _failpoints.Hit(Failpoint.AfterSimulatedCloudCatalogPrimaryWrite);
            ConvergeCatalogMirror(bytes);
            _lease.ValidateRemoteAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            _failpoints.Hit(Failpoint.AfterSimulatedCloudCatalogMirrorWrite);
        }
        catch (Exception exception)
        {
            throw CatalogPublicationUnknown(exception);
        }
    }

    void ConvergeCatalogMirror(ReadOnlyMemory<byte> bytes)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            _lease.ValidateRemoteAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            _failpoints.Hit(Failpoint.BeforeSimulatedCloudCatalogMirrorWrite);
            var written = _objectStore.PutAsync(PantsCloudObjectLayout.WalCatalogMirrorObjectKey, bytes,
                    _catalogMirrorVersion is null
                        ? new CloudObjectWriteCondition.IfAbsent()
                        : new CloudObjectWriteCondition.IfVersion(_catalogMirrorVersion),
                    CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            if (written)
            {
                _catalogMirrorVersion = CreateVersion(bytes.Span);
                _catalogMirrorPending = false;
                return;
            }

            var primary = _objectStore.HeadAsync(PantsCloudObjectLayout.WalCatalogObjectKey)
                .AsTask().GetAwaiter().GetResult();
            if (primary is null || primary.Version != _catalogPrimaryVersion ||
                primary.SizeBytes != checked((ulong)bytes.Length))
            {
                throw new PantsFencedException("A successor owns simulated cloud catalog mirror convergence.");
            }

            var mirror = _objectStore.HeadAsync(PantsCloudObjectLayout.WalCatalogMirrorObjectKey)
                .AsTask().GetAwaiter().GetResult();
            _catalogMirrorVersion = mirror?.Version;
            // The next iteration validates the full lease again before using this observation.
        }

        throw new PantsBusyException("Simulated cloud catalog mirror convergence exceeded its bounded CAS retries.");
    }

    PantsLeaseIndeterminateException CatalogPublicationUnknown(Exception exception)
    {
        Volatile.Write(ref _persistenceAnomaly, 1);
        return new PantsLeaseIndeterminateException(
            "The simulated cloud WAL catalog may be published; reconcile its exact primary before retrying.",
            exception);
    }

    void PruneCoveredWal(CloudControlMetadataSnapshot metadata)
    {
        var manifest = metadata.AuthoritativeManifest;
        if (manifest is null)
        {
            return;
        }

        var historyStartChanged = false;
        if (_catalog.WalHistoryStartSegmentId < manifest.WalHistoryStart)
        {
            _catalog.WalHistoryStartSegmentId = manifest.WalHistoryStart;
            historyStartChanged = true;
        }

        if (_catalog.NextSegmentId < manifest.WalHistoryStart)
        {
            _catalog.NextSegmentId = manifest.WalHistoryStart;
            historyStartChanged = true;
        }

        var coveredSequence = new WalCoverageIndex(manifest).MaximumSequenceWithDurableEvidence;
        if (coveredSequence == 0)
        {
            if (historyStartChanged)
            {
                SaveCatalog();
            }

            return;
        }

        var candidates = _catalog.Segments.Values
            .Where(segment => segment.MaximumSequence <= coveredSequence)
            .ToArray();
        if (candidates.Length == 0)
        {
            if (historyStartChanged)
            {
                SaveCatalog();
            }

            return;
        }

        var retired = new List<PublishedWalSegment>(candidates.Length);
        var walBytes = new Dictionary<ulong, byte[]>();
        var walGuards = new Dictionary<ulong, SimulatedCloudObjectGuard>();
        foreach (var segment in candidates)
        {
            var path = ResolveObjectPath(_cloudRoot, segment.ObjectKey);
            if (!File.Exists(path))
            {
                throw new PantsRecoveryFailedException(
                    $"Published simulated-cloud WAL '{segment.ObjectKey}' is missing during pruning.");
            }

            var bytes = File.ReadAllBytes(path);
            if (checked((ulong)bytes.Length) != segment.SizeBytes ||
                DiskFormat.Crc32C(bytes) != segment.ContentCrc32C)
            {
                throw new PantsCorruptionException(
                    $"Published simulated-cloud WAL '{segment.ObjectKey}' differs from its catalog proof.");
            }

            if (!CloudWalCoverageValidator.ValidateAndIsCovered(
                    bytes,
                    segment.MaximumSequence,
                    segment.WriterEpoch,
                    (ManifestState)manifest))
            {
                // Retire only the oldest covered prefix so recovery never has to jump over a gap.
                break;
            }

            retired.Add(segment);
            walBytes.Add(segment.SegmentId, bytes);
            walGuards.Add(
                segment.SegmentId,
                new SimulatedCloudObjectGuard(path, CreateVersion(bytes)));
        }

        if (retired.Count == 0)
        {
            if (historyStartChanged)
            {
                SaveCatalog();
            }

            return;
        }

        var dependencyGuards = ValidateManifestDependencies((ManifestState)manifest, metadata);
        VerifyIdentityGuards(dependencyGuards.Concat(walGuards.Values));
        foreach (var segment in retired)
        {
            CloudWalCoverageValidator.ValidateAndEnsureCovered(
                walBytes[segment.SegmentId],
                segment.MaximumSequence,
                segment.WriterEpoch,
                (ManifestState)manifest);
        }

        var generation = CloudMetadataAuthority.ReadGenerationOrdinal(File.ReadAllBytes(
            ResolveObjectPath(_cloudRoot, CloudMetadataAuthority.PointerKey)));
        if (generation == 0)
        {
            throw new PantsCorruptionException("WAL retirement has no committed metadata generation.");
        }

        _catalog.Anchor = retired[^1].Facts!.Link;
        _catalog.RetiredBy = Math.Max(_catalog.RetiredBy, generation);
        foreach (var segment in retired)
        {
            _catalog.Segments.Remove(segment.SegmentId);
        }

        _catalog.NextSegmentId = Math.Max(
            _catalog.NextSegmentId,
            manifest.WalHistoryStart);

        SaveCatalog();
        foreach (var segment in retired)
        {
            var guard = walGuards[segment.SegmentId];
            if (File.Exists(guard.Path) &&
                StringComparer.Ordinal.Equals(
                    CreateFileVersion(guard.Path),
                    guard.Version))
            {
                File.Delete(guard.Path);
            }
        }
    }

    List<SimulatedCloudObjectGuard> ValidateManifestDependencies(
        ManifestState manifest,
        CloudControlMetadataSnapshot metadata)
    {
        var guards = new List<SimulatedCloudObjectGuard>(
            manifest.Files.Count + MetadataFiles.Length);
        foreach (var file in manifest.Files)
        {
            var path = ResolveObjectPath(
                _cloudRoot,
                PantsCloudObjectLayout.SstPrefix + file.Name);
            if (!File.Exists(path))
            {
                throw new PantsRecoveryFailedException(
                    $"Manifest simulated-cloud SST '{file.Name}' is missing during WAL pruning.");
            }

            var length = new FileInfo(path).Length;
            if ((file.SizeBytes != 0 && checked((ulong)length) != file.SizeBytes) ||
                (file.ContentCrc32C.HasValue &&
                 ComputeFileCrc32C(path) != file.ContentCrc32C.Value))
            {
                throw new PantsCorruptionException(
                    $"Manifest simulated-cloud SST '{file.Name}' differs during WAL pruning.");
            }

            var source = LocalAsyncSstSource.Open(path);
            var reader = AsyncSstReader.OpenAsync(source, file, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            reader.DisposeAsync().AsTask().GetAwaiter().GetResult();

            guards.Add(new SimulatedCloudObjectGuard(path, CreateFileVersion(path)));
        }

        var pointerPath = ResolveObjectPath(_cloudRoot, CloudMetadataAuthority.PointerKey);
        var pointerBytes = File.ReadAllBytes(pointerPath);
        var selected = CloudMetadataAuthority.ReadCommittedFilesAsync(_objectStore, CancellationToken.None, _metadataSnapshotBudget).AsTask().GetAwaiter().GetResult() ??
            throw new PantsRecoveryFailedException("Simulated cloud metadata has no committed generation.");
        foreach (var fileName in MetadataFiles)
        {
            if (selected.TryGetValue(fileName, out var committed) != metadata.Files.TryGetValue(fileName, out var captured) ||
                selected.ContainsKey(fileName) && !committed.Span.SequenceEqual(captured.Span))
            {
                throw new PantsCorruptionException("Simulated cloud committed metadata differs from the captured publication.");
            }
        }

        guards.Add(new SimulatedCloudObjectGuard(pointerPath, CreateVersion(pointerBytes)));

        return guards;
    }

    static void VerifyIdentityGuards(IEnumerable<SimulatedCloudObjectGuard> guards)
    {
        foreach (var guard in guards)
        {
            if (!File.Exists(guard.Path) ||
                !StringComparer.Ordinal.Equals(
                    CreateFileVersion(guard.Path),
                    guard.Version))
            {
                throw new PantsLeaseIndeterminateException(
                    $"Simulated-cloud WAL pruning dependency '{guard.Path}' changed after validation.");
            }
        }
    }

    static void CopyIfPresent(string source, string destination)
    {
        if (File.Exists(source) && !File.Exists(destination))
        {
            AtomicStagedFile.Write(destination, File.ReadAllBytes(source));
        }
    }

    static string ResolveObjectPath(string cloudRoot, string objectKey)
    {
        if (string.IsNullOrEmpty(objectKey) ||
            objectKey.StartsWith('/') ||
            objectKey.Contains("..", StringComparison.Ordinal) ||
            objectKey.Contains('\\'))
        {
            throw PantsException.Create(PantsErrorCode.Corruption, "A cloud object key is unsafe.");
        }

        return Path.Combine(cloudRoot, objectKey.Replace('/', Path.DirectorySeparatorChar));
    }

    static string CreateVersion(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    static string CreateFileVersion(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    static uint ComputeFileCrc32C(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[64 * 1024];
        var checksum = 0U;
        int read;
        while ((read = stream.Read(buffer)) != 0)
        {
            checksum = DiskFormat.Crc32CAppend(checksum, buffer.AsSpan(0, read));
        }

        return checksum;
    }

    sealed class WalPublicationCatalog
    {
        public WalChainLink Anchor { get; set; } = WalChainLink.Genesis;

        public ulong SequenceFloor { get; set; }

        public ulong RetiredBy { get; set; }

        public uint FormatVersion { get; set; }

        public ulong FencingEpoch { get; set; }

        public ulong NextSegmentId { get; set; }

        public ulong WalHistoryStartSegmentId { get; set; }

        public SortedDictionary<ulong, PublishedWalSegment> Segments { get; set; } = [];

        public static WalPublicationCatalog Empty(ulong fencingEpoch) => new()
        {
            FormatVersion = CatalogFormatVersion,
            FencingEpoch = fencingEpoch,
            NextSegmentId = 1
        };
    }

    sealed class PublishedWalSegment : IEquatable<PublishedWalSegment>
    {
        public WalChainLink? Previous { get; set; }

        public WalSealedFacts? Facts { get; set; }

        public ulong SegmentId { get; set; }

        public ulong WriterEpoch { get; set; }

        [JsonPropertyName("max_sequence")] public ulong MaximumSequence { get; set; }

        public ulong SizeBytes { get; set; }

        [JsonPropertyName("content_crc32c")] public uint ContentCrc32C { get; set; }

        public string ObjectKey { get; set; } = string.Empty;

        public bool Equals(PublishedWalSegment? other) =>
            other is not null &&
            SegmentId == other.SegmentId &&
            WriterEpoch == other.WriterEpoch &&
            MaximumSequence == other.MaximumSequence &&
            SizeBytes == other.SizeBytes &&
            ContentCrc32C == other.ContentCrc32C &&
            ObjectKey == other.ObjectKey;

        public override bool Equals(object? obj) => Equals(obj as PublishedWalSegment);

        public override int GetHashCode() => HashCode.Combine(
            SegmentId,
            WriterEpoch,
            MaximumSequence,
            SizeBytes,
            ContentCrc32C,
            ObjectKey);
    }
}
