using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Pants.Cloud.Internal;

sealed class SimulatedCloudPersistence : ICloudPersistence
{
    const uint CatalogFormatVersion = 1;

    static readonly string[] MetadataFiles =
    [
        "FORMAT",
        "manifest.snapshot.json",
        "manifest.json",
        "manifest.journal",
        "intent_log.json"
    ];

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    WalPublicationCatalog _catalog;
    readonly string _cloudRoot;
    readonly IFailpointHandler _failpoints;

    readonly string _localRoot;
    readonly ulong _writerEpoch;
    int _persistenceAnomaly;

    public SimulatedCloudPersistence(
        string localRoot,
        ulong writerEpoch,
        IFailpointHandler? failpoints = null)
    {
        _localRoot = Path.GetFullPath(localRoot);
        _cloudRoot = Path.Combine(_localRoot, "cloud_store");
        _writerEpoch = writerEpoch;
        _failpoints = failpoints ?? NullPantsFailpointHandler.Instance;
        Directory.CreateDirectory(_cloudRoot);
        _catalog = LoadCatalog(_cloudRoot) ?? WalPublicationCatalog.Empty(writerEpoch);
        if (_catalog.FencingEpoch > writerEpoch)
        {
            throw PantsException.Create(
                PantsErrorCode.Fenced,
                $"The cloud WAL catalog is fenced at epoch {_catalog.FencingEpoch}.");
        }

        _catalog.FencingEpoch = writerEpoch;
        SaveCatalog();
        MirrorMetadataAndSsts();
    }

    public bool HasPersistenceAnomaly => Volatile.Read(ref _persistenceAnomaly) != 0;

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
            CreateVersion(bytes)));
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
        CancellationToken cancellationToken)
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

        AtomicStagedFile.Write(path, CloudDdlJson.SerializeRegistry(registry));
        return ValueTask.FromResult(true);
    }

    public ValueTask PublishWalBatchAsync(
        IReadOnlyList<SealedWalSegment> segments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PublishWalBatch(segments);
        return ValueTask.CompletedTask;
    }

    public ValueTask MirrorMetadataAndSstsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MirrorMetadataAndSsts();
        return ValueTask.CompletedTask;
    }

    public ValueTask CollectObsoleteSstsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CollectObsoleteSsts();
        return ValueTask.CompletedTask;
    }

    public ValueTask ValidateWriteAuthorityAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_catalog.FencingEpoch != _writerEpoch)
        {
            throw new PantsFencedException(
                "The simulated-cloud WAL catalog is not fenced to this writer.");
        }

        return ValueTask.CompletedTask;
    }

    public static SimulatedCloudHydrationResult PrepareLocalCache(
        string localRoot,
        PantsRecoveryPolicy recoveryPolicy)
    {
        var root = Path.GetFullPath(localRoot);
        var cloudRoot = Path.Combine(root, "cloud_store");
        if (!Directory.Exists(cloudRoot))
        {
            return new SimulatedCloudHydrationResult(
                0,
                false);
        }

        var localManifest = CloudManifestReader.ReadManifest(root);
        var remoteManifestPath = File.Exists(Path.Combine(
            cloudRoot,
            "metadata",
            "manifest.snapshot.json"))
            ? Path.Combine(cloudRoot, "metadata", "manifest.snapshot.json")
            : Path.Combine(cloudRoot, "metadata", "manifest.json");
        var remoteManifest = File.Exists(remoteManifestPath)
            ? CloudManifestReader.DecodeManifest(File.ReadAllBytes(remoteManifestPath))
            : null;
        foreach (var fileName in MetadataFiles)
        {
            CopyIfPresent(
                Path.Combine(cloudRoot, "metadata", fileName),
                Path.Combine(root, fileName));
        }

        var activeManifest = localManifest ?? remoteManifest;

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

        var catalog = LoadCatalog(cloudRoot);
        if (catalog is null)
        {
            return new SimulatedCloudHydrationResult(0, false);
        }

        var localWalDirectory = Path.Combine(root, "wal");
        Directory.CreateDirectory(localWalDirectory);
        var requiresSalvage = false;
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

            var localName = $"{segmentId:00000000000000000000}.wal";
            AtomicStagedFile.Write(Path.Combine(localWalDirectory, localName), bytes);
        }

        return new SimulatedCloudHydrationResult(
            catalog.FencingEpoch,
            requiresSalvage);
    }

    public void PublishWal(SealedWalSegment segment) => PublishWalBatch([segment]);

    public void PublishWalBatch(IReadOnlyList<SealedWalSegment> segments)
    {
        if (segments.Count == 0)
        {
            return;
        }

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

        var publications = segments.Select(segment => new PublishedWalSegment
        {
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
                segments[index].Bytes);
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        var updatedSegments = new SortedDictionary<ulong, PublishedWalSegment>(_catalog.Segments);
        foreach (var publication in publications)
        {
            updatedSegments[publication.SegmentId] = publication;
        }

        var updatedCatalog = new WalPublicationCatalog
        {
            FormatVersion = _catalog.FormatVersion,
            FencingEpoch = _catalog.FencingEpoch,
            Segments = updatedSegments
        };
        SaveCatalog(updatedCatalog);
        _catalog = updatedCatalog;
        MirrorMetadataAndSsts();
    }

    public void MirrorMetadataAndSsts()
    {
        var metadata = CloudControlMetadataSnapshot.Capture(_localRoot, MetadataFiles);
        var localSstDirectory = Path.Combine(_localRoot, "sst");
        var localSsts = Directory.Exists(localSstDirectory)
            ? Directory.EnumerateFiles(
                    localSstDirectory,
                    "*.sst",
                    SearchOption.TopDirectoryOnly)
                .ToArray()
            : [];
        var pendingSsts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var localSst in localSsts)
        {
            var name = ValidateSstName(Path.GetFileName(localSst));
            var remotePath = Path.Combine(_cloudRoot, "sst", name);
            var bytes = File.ReadAllBytes(localSst);
            if (!File.Exists(remotePath))
            {
                pendingSsts.Add(remotePath, bytes);
                continue;
            }

            if (!File.ReadAllBytes(remotePath).AsSpan().SequenceEqual(bytes))
            {
                throw new PantsFencedException(
                    $"Immutable simulated-cloud SST '{Path.GetFileName(localSst)}' conflicts.");
            }
        }

        foreach (var references in metadata.ReferencedSsts
                     .GroupBy(static file => file.Name, StringComparer.Ordinal))
        {
            var name = ValidateSstName(references.Key);
            var localPath = Path.Combine(localSstDirectory, name);
            var remotePath = Path.Combine(_cloudRoot, "sst", name);
            if (File.Exists(localPath) && !File.Exists(remotePath))
            {
                var bytes = File.ReadAllBytes(localPath);
                foreach (var proof in references)
                {
                    CloudSstValidator.Validate(bytes, proof);
                }

                pendingSsts.TryAdd(remotePath, bytes);
            }
        }

        if (pendingSsts.Count > 0)
        {
            _failpoints.Hit(Failpoint.BeforeCloudUpload);
            foreach (var (remotePath, bytes) in pendingSsts)
            {
                AtomicStagedFile.Write(remotePath, bytes);
            }

            _failpoints.Hit(Failpoint.AfterCloudUpload);
        }

        ValidateCapturedSstMetadata(metadata);

        foreach (var fileName in MetadataFiles)
        {
            if (metadata.Files.TryGetValue(fileName, out var bytes))
            {
                WriteIfChanged(
                    Path.Combine(_cloudRoot, "metadata", fileName),
                    bytes.Span);
            }
        }

        CollectObsoleteSsts();

        PruneCoveredWal(metadata);
    }

    void ValidateCapturedSstMetadata(CloudControlMetadataSnapshot metadata)
    {
        foreach (var file in metadata.ReferencedSsts)
        {
            var name = ValidateSstName(file.Name);
            var path = Path.Combine(_cloudRoot, "sst", name);
            if (!File.Exists(path))
            {
                throw new PantsRecoveryFailedException(
                    $"Manifest simulated-cloud SST '{name}' is unavailable for publication.");
            }

            var source = LocalAsyncSstSource.Open(path);
            var reader = AsyncSstReader.OpenAsync(source, file, CancellationToken.None)
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
                _failpoints))
        {
            Volatile.Write(ref _persistenceAnomaly, 1);
        }
    }

    static WalPublicationCatalog? LoadCatalog(string cloudRoot)
    {
        var path = Path.Combine(cloudRoot, "wal", "publication-catalog.v1.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var catalog = JsonSerializer.Deserialize<WalPublicationCatalog>(
                File.ReadAllBytes(path),
                JsonOptions) ?? throw new JsonException("Cloud WAL catalog is empty.");
            if (catalog.FormatVersion != CatalogFormatVersion || catalog.FencingEpoch == 0)
            {
                throw new JsonException("Cloud WAL catalog header is invalid.");
            }

            foreach (var (segmentId, publication) in catalog.Segments)
            {
                ValidatePublication(segmentId, publication, catalog.FencingEpoch);
            }

            return catalog;
        }
        catch (JsonException exception)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Cloud WAL publication catalog v1 cannot be decoded.",
                exception);
        }
    }

    static void ValidatePublication(
        ulong segmentId,
        PublishedWalSegment publication,
        ulong fencingEpoch)
    {
        if (segmentId == 0 || publication.WriterEpoch == 0)
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

    void SaveCatalog(WalPublicationCatalog catalog) => AtomicStagedFile.Write(
        Path.Combine(_cloudRoot, "wal", "publication-catalog.v1.json"),
        JsonSerializer.SerializeToUtf8Bytes(catalog, JsonOptions));

    void PruneCoveredWal(CloudControlMetadataSnapshot metadata)
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

        var candidates = _catalog.Segments.Values
            .Where(segment => segment.MaximumSequence <= coveredSequence)
            .ToArray();
        if (candidates.Length == 0)
        {
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
                continue;
            }

            retired.Add(segment);
            walBytes.Add(segment.SegmentId, bytes);
            walGuards.Add(
                segment.SegmentId,
                new SimulatedCloudObjectGuard(path, CreateVersion(bytes)));
        }

        if (retired.Count == 0)
        {
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

        foreach (var segment in retired)
        {
            _catalog.Segments.Remove(segment.SegmentId);
        }

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

        foreach (var (fileName, capturedBytes) in metadata.Files)
        {
            var remotePath = Path.Combine(_cloudRoot, "metadata", fileName);
            if (!File.Exists(remotePath))
            {
                throw new PantsRecoveryFailedException(
                    $"Simulated-cloud metadata object '{fileName}' is missing during WAL pruning.");
            }

            var remoteBytes = File.ReadAllBytes(remotePath);
            if (!remoteBytes.AsSpan().SequenceEqual(capturedBytes.Span))
            {
                throw new PantsCorruptionException(
                    $"Simulated-cloud metadata object '{fileName}' differs from published snapshot bytes.");
            }

            guards.Add(new SimulatedCloudObjectGuard(
                remotePath,
                CreateVersion(remoteBytes)));
        }

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
        public uint FormatVersion { get; set; }

        public ulong FencingEpoch { get; set; }

        public SortedDictionary<ulong, PublishedWalSegment> Segments { get; set; } = [];

        public static WalPublicationCatalog Empty(ulong fencingEpoch) => new()
        {
            FormatVersion = CatalogFormatVersion,
            FencingEpoch = fencingEpoch
        };
    }

    sealed class PublishedWalSegment : IEquatable<PublishedWalSegment>
    {
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
