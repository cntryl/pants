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

    public SimulatedCloudPersistence(string localRoot, ulong writerEpoch)
    {
        _localRoot = Path.GetFullPath(localRoot);
        _cloudRoot = Path.Combine(_localRoot, "cloud_store");
        _writerEpoch = writerEpoch;
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
        EnsureDdlRegistry();
    }

    public static ulong PrepareLocalCache(string localRoot)
    {
        var root = Path.GetFullPath(localRoot);
        var cloudRoot = Path.Combine(root, "cloud_store");
        if (!Directory.Exists(cloudRoot))
        {
            return 0;
        }

        foreach (var fileName in MetadataFiles)
        {
            CopyIfPresent(
                Path.Combine(cloudRoot, "metadata", fileName),
                Path.Combine(root, fileName));
        }

        var cloudSstDirectory = Path.Combine(cloudRoot, "sst");
        if (Directory.Exists(cloudSstDirectory))
        {
            var localSstDirectory = Path.Combine(root, "sst");
            Directory.CreateDirectory(localSstDirectory);
            foreach (var cloudSst in Directory.EnumerateFiles(
                         cloudSstDirectory,
                         "*.sst",
                         SearchOption.TopDirectoryOnly))
            {
                CopyIfPresent(cloudSst, Path.Combine(localSstDirectory, Path.GetFileName(cloudSst)));
            }
        }

        var catalog = LoadCatalog(cloudRoot);
        if (catalog is null)
        {
            return 0;
        }

        var localWalDirectory = Path.Combine(root, "wal");
        Directory.CreateDirectory(localWalDirectory);
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
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    $"Published cloud WAL object '{publication.ObjectKey}' failed catalog validation.");
            }

            var localName = $"{segmentId:00000000000000000000}.wal";
            AtomicStagedFile.Write(Path.Combine(localWalDirectory, localName), bytes);
        }

        return catalog.FencingEpoch;
    }

    public void PublishWal(SealedWalSegment segment)
    {
        if (segment.WriterEpoch != _writerEpoch)
        {
            throw PantsException.Create(
                PantsErrorCode.Fenced,
                "A WAL segment from a stale writer epoch cannot be published.");
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
        foreach (var localSst in localSsts)
        {
            AtomicStagedFile.Write(
                Path.Combine(_cloudRoot, "sst", Path.GetFileName(localSst)),
                File.ReadAllBytes(localSst));
        }

        var retainedNames = localSsts
            .Select(static path => Path.GetFileName(path))
            .ToHashSet(StringComparer.Ordinal);
        var cloudSstDirectory = Path.Combine(_cloudRoot, "sst");
        Directory.CreateDirectory(cloudSstDirectory);
        foreach (var cloudSst in Directory.EnumerateFiles(
                     cloudSstDirectory,
                     "*.sst",
                     SearchOption.TopDirectoryOnly))
        {
            if (!retainedNames.Contains(Path.GetFileName(cloudSst)))
            {
                File.Delete(cloudSst);
            }
        }
    }

    public void PublishColumnFamilyCreate(MidgeColumnFamilyMeta metadata)
    {
        var registry = LoadDdlRegistry();
        if (registry.ColumnFamilies.Any(family => family.Id == metadata.Id))
        {
            throw PantsException.Create(
                PantsErrorCode.Fenced,
                $"The cloud DDL registry already contains column family {metadata.Id}.");
        }

        registry.ColumnFamilies.Add(metadata.Clone());
        registry.Epoch = checked(registry.Epoch + 1);
        registry.Operations.Add(new DdlOperation
        {
            OperationId = Guid.NewGuid().ToString(),
            Edit = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["CreateColumnFamily"] = new
                {
                    id = metadata.Id,
                    name = metadata.Name,
                    created_at = metadata.CreatedAt
                }
            }
        });
        SaveDdlRegistry(registry);
    }

    public void PublishColumnFamilyDrop(MidgeColumnFamilyMeta metadata)
    {
        var registry = LoadDdlRegistry();
        var index = registry.ColumnFamilies.FindIndex(family => family.Id == metadata.Id);
        if (index < 0)
        {
            throw PantsException.Create(
                PantsErrorCode.Fenced,
                $"The cloud DDL registry does not contain column family {metadata.Id}.");
        }

        registry.ColumnFamilies[index] = metadata.Clone();
        registry.Epoch = checked(registry.Epoch + 1);
        registry.Operations.Add(new DdlOperation
        {
            OperationId = Guid.NewGuid().ToString(),
            Edit = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["DropColumnFamilyAt"] = new
                {
                    id = metadata.Id,
                    drop_sequence = metadata.DropSequence ?? 0,
                    dropped_sst_names = metadata.DroppedSstNames
                }
            }
        });
        SaveDdlRegistry(registry);
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

    public ValueTask PublishColumnFamilyCreateAsync(
        MidgeColumnFamilyMeta metadata,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PublishColumnFamilyCreate(metadata);
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishColumnFamilyDropAsync(
        MidgeColumnFamilyMeta metadata,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PublishColumnFamilyDrop(metadata);
        return ValueTask.CompletedTask;
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

    void EnsureDdlRegistry()
    {
        var path = ResolveObjectPath(_cloudRoot, PantsCloudObjectLayout.DdlRegistryObjectKey);
        if (!File.Exists(path))
        {
            AtomicStagedFile.Write(path, "{\n  \"epoch\": 0,\n  \"column_families\": [],\n  \"operations\": []\n}"u8);
        }
    }

    DdlRegistry LoadDdlRegistry()
    {
        EnsureDdlRegistry();
        try
        {
            return JsonSerializer.Deserialize<DdlRegistry>(
                File.ReadAllBytes(ResolveObjectPath(
                    _cloudRoot,
                    PantsCloudObjectLayout.DdlRegistryObjectKey)),
                JsonOptions) ?? throw new JsonException("Cloud DDL registry is empty.");
        }
        catch (JsonException exception)
        {
            throw PantsException.Create(
                PantsErrorCode.Corruption,
                "Cloud DDL registry cannot be decoded.",
                exception);
        }
    }

    void SaveDdlRegistry(DdlRegistry registry) => AtomicStagedFile.Write(
        ResolveObjectPath(_cloudRoot, PantsCloudObjectLayout.DdlRegistryObjectKey),
        JsonSerializer.SerializeToUtf8Bytes(registry, JsonOptions));

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

    sealed class DdlRegistry
    {
        public ulong Epoch { get; set; }

        public List<MidgeColumnFamilyMeta> ColumnFamilies { get; set; } = [];

        public List<DdlOperation> Operations { get; set; } = [];
    }

    sealed class DdlOperation
    {
        [JsonPropertyName("op_id")]
        public string OperationId { get; set; } = string.Empty;

        public Dictionary<string, object> Edit { get; set; } = [];
    }
}
