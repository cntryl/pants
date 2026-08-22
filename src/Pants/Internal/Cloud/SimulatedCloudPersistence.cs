using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pants;

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

    readonly string _localRoot;
    readonly string _cloudRoot;
    readonly ulong _writerEpoch;
    readonly WalPublicationCatalog _catalog;
    readonly IPantsFailpointHandler _failpoints;
    int _persistenceAnomaly;

    public bool HasPersistenceAnomaly => Volatile.Read(ref _persistenceAnomaly) != 0;

    public SimulatedCloudPersistence(
        string localRoot,
        ulong writerEpoch,
        IPantsFailpointHandler? failpoints = null)
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
                new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal),
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

        var recoverySsts = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        var localSstDirectory = Path.Combine(root, "sst");
        Directory.CreateDirectory(localSstDirectory);
        foreach (var file in activeManifest?.Files ?? [])
        {
            var localPath = Path.Combine(localSstDirectory, file.Name);
            var cloudPath = Path.Combine(cloudRoot, "sst", file.Name);
            if (!File.Exists(cloudPath))
            {
                var isRemoteAuthoritative = localManifest is null || remoteManifest?.Files.Any(
                    remoteFile => StringComparer.Ordinal.Equals(
                        remoteFile.Name,
                        file.Name)) == true;
                if (isRemoteAuthoritative || !File.Exists(localPath))
                {
                    throw new PantsRecoveryFailedException(
                        $"Authoritative simulated-cloud SST '{file.Name}' is missing.");
                }

                continue;
            }

            var bytes = File.ReadAllBytes(cloudPath);
            CloudSstValidator.Validate(bytes, file);
            if (File.Exists(localPath))
            {
                continue;
            }

            if (localManifest is not null)
            {
                recoverySsts[file.Name] = bytes;
            }
            else
            {
                AtomicStagedFile.Write(localPath, bytes);
            }
        }

        var catalog = LoadCatalog(cloudRoot);
        if (catalog is null)
        {
            return new SimulatedCloudHydrationResult(0, recoverySsts, false);
        }

        var localWalDirectory = Path.Combine(root, "wal");
        Directory.CreateDirectory(localWalDirectory);
        var requiresSalvage = false;
        foreach ((var segmentId, var publication) in catalog.Segments)
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
                MidgeDiskFormat.Crc32C(bytes) != publication.ContentCrc32C)
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
            recoverySsts,
            requiresSalvage);
    }

    public void PublishWal(SealedWalSegment segment)
    {
        if (segment.WriterEpoch > _writerEpoch)
        {
            throw PantsException.Create(
                PantsErrorCode.Fenced,
                "A WAL segment from a future writer epoch cannot be published.");
        }

        var objectKey = PantsCloudObjectLayout.WalSegmentObjectKey(
            segment.WriterEpoch,
            segment.SegmentId);
        var publication = new PublishedWalSegment
        {
            SegmentId = segment.SegmentId,
            WriterEpoch = segment.WriterEpoch,
            MaximumSequence = segment.MaximumSequence,
            SizeBytes = checked((ulong)segment.Bytes.Length),
            ContentCrc32C = MidgeDiskFormat.Crc32C(segment.Bytes),
            ObjectKey = objectKey
        };

        if (_catalog.Segments.TryGetValue(segment.SegmentId, out var existing))
        {
            if (!existing.Equals(publication))
            {
                throw PantsException.Create(
                    PantsErrorCode.Fenced,
                    $"Cloud WAL segment {segment.SegmentId} conflicts with its publication catalog entry.");
            }

            return;
        }

        AtomicStagedFile.Write(ResolveObjectPath(_cloudRoot, objectKey), segment.Bytes);
        _catalog.Segments.Add(segment.SegmentId, publication);
        SaveCatalog();
        MirrorMetadataAndSsts();
    }

    public void MirrorMetadataAndSsts()
    {
        var localSstDirectory = Path.Combine(_localRoot, "sst");
        if (!Directory.Exists(localSstDirectory))
        {
            return;
        }

        var localSsts = Directory.EnumerateFiles(
                localSstDirectory,
                "*.sst",
                SearchOption.TopDirectoryOnly)
            .ToArray();
        if (localSsts.Length > 0)
        {
            _failpoints.Hit(PantsFailpoint.BeforeCloudUpload);
        }

        foreach (var localSst in localSsts)
        {
            AtomicStagedFile.Write(
                Path.Combine(_cloudRoot, "sst", Path.GetFileName(localSst)),
                File.ReadAllBytes(localSst));
        }

        if (localSsts.Length > 0)
        {
            _failpoints.Hit(PantsFailpoint.AfterCloudUpload);
        }

        foreach (var fileName in MetadataFiles)
        {
            var localPath = Path.Combine(_localRoot, fileName);
            if (File.Exists(localPath))
            {
                AtomicStagedFile.Write(
                    Path.Combine(_cloudRoot, "metadata", fileName),
                    File.ReadAllBytes(localPath));
            }
        }

        CollectObsoleteSsts();

        PruneCoveredWal(CloudManifestReader.ReadLastPersistedSequence(_localRoot));
    }

    public ValueTask<ReadOnlyMemory<byte>?> FetchSstAsync(
        string name,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveObjectPath(
            _cloudRoot,
            PantsCloudObjectLayout.SstPrefix + name);
        return ValueTask.FromResult<ReadOnlyMemory<byte>?>(
            File.Exists(path) ? File.ReadAllBytes(path) : null);
    }

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
        return ValueTask.FromResult<CloudDdlRegistryObject?>(new(
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
                     CreateVersion(File.ReadAllBytes(path)),
                     expectedVersion))
        {
            return ValueTask.FromResult(false);
        }

        AtomicStagedFile.Write(path, CloudDdlJson.SerializeRegistry(registry));
        return ValueTask.FromResult(true);
    }

    public ValueTask PublishWalAsync(
        SealedWalSegment segment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PublishWal(segment);
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

            foreach ((var segmentId, var publication) in catalog.Segments)
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

    void SaveCatalog() => AtomicStagedFile.Write(
        Path.Combine(_cloudRoot, "wal", "publication-catalog.v1.json"),
        JsonSerializer.SerializeToUtf8Bytes(_catalog, JsonOptions));

    void PruneCoveredWal(ulong coveredSequence)
    {
        if (coveredSequence == 0)
        {
            return;
        }

        var manifest = CloudManifestReader.ReadManifest(_localRoot) ??
            throw new PantsCorruptionException(
                "Simulated-cloud WAL pruning requires a committed local manifest.");
        if (manifest.LastPersistedSequence != coveredSequence)
        {
            throw new PantsCorruptionException(
                "Simulated-cloud WAL pruning sequence differs from the committed manifest.");
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
                MidgeDiskFormat.Crc32C(bytes) != segment.ContentCrc32C)
            {
                throw new PantsCorruptionException(
                    $"Published simulated-cloud WAL '{segment.ObjectKey}' differs from its catalog proof.");
            }

            if (!CloudWalCoverageValidator.ValidateAndIsCovered(
                    bytes,
                    segment.MaximumSequence,
                    segment.WriterEpoch,
                    manifest))
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

        var dependencyGuards = ValidateManifestDependencies(manifest);
        VerifyIdentityGuards(dependencyGuards.Concat(walGuards.Values));
        foreach (var segment in retired)
        {
            CloudWalCoverageValidator.ValidateAndEnsureCovered(
                walBytes[segment.SegmentId],
                segment.MaximumSequence,
                segment.WriterEpoch,
                manifest);
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
                    CreateVersion(File.ReadAllBytes(guard.Path)),
                    guard.Version))
            {
                File.Delete(guard.Path);
            }
        }
    }

    List<SimulatedCloudObjectGuard> ValidateManifestDependencies(
        MidgeManifest manifest)
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

            var bytes = File.ReadAllBytes(path);
            CloudSstValidator.Validate(bytes, file);

            guards.Add(new SimulatedCloudObjectGuard(path, CreateVersion(bytes)));
        }

        foreach (var fileName in MetadataFiles)
        {
            var localPath = Path.Combine(_localRoot, fileName);
            if (!File.Exists(localPath))
            {
                continue;
            }

            var remotePath = Path.Combine(_cloudRoot, "metadata", fileName);
            if (!File.Exists(remotePath))
            {
                throw new PantsRecoveryFailedException(
                    $"Simulated-cloud metadata object '{fileName}' is missing during WAL pruning.");
            }

            var localBytes = File.ReadAllBytes(localPath);
            var remoteBytes = File.ReadAllBytes(remotePath);
            if (!remoteBytes.AsSpan().SequenceEqual(localBytes))
            {
                throw new PantsCorruptionException(
                    $"Simulated-cloud metadata object '{fileName}' differs from local committed bytes.");
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
                    CreateVersion(File.ReadAllBytes(guard.Path)),
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

        [JsonPropertyName("max_sequence")]
        public ulong MaximumSequence { get; set; }

        public ulong SizeBytes { get; set; }

        [JsonPropertyName("content_crc32c")]
        public uint ContentCrc32C { get; set; }

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
