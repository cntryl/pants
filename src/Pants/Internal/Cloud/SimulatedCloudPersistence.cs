using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pants;

internal sealed class SimulatedCloudPersistence
{
    private const uint CatalogFormatVersion = 1;
    private static readonly string[] MetadataFiles =
    [
        "FORMAT",
        "manifest.snapshot.json",
        "manifest.json",
        "manifest.journal",
        "intent_log.json"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _localRoot;
    private readonly string _cloudRoot;
    private readonly ulong _writerEpoch;
    private readonly WalPublicationCatalog _catalog;

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
        string root = Path.GetFullPath(localRoot);
        string cloudRoot = Path.Combine(root, "cloud_store");
        if (!Directory.Exists(cloudRoot))
        {
            return 0;
        }

        foreach (string fileName in MetadataFiles)
        {
            CopyIfPresent(
                Path.Combine(cloudRoot, "metadata", fileName),
                Path.Combine(root, fileName));
        }

        string cloudSstDirectory = Path.Combine(cloudRoot, "sst");
        if (Directory.Exists(cloudSstDirectory))
        {
            string localSstDirectory = Path.Combine(root, "sst");
            Directory.CreateDirectory(localSstDirectory);
            foreach (string cloudSst in Directory.EnumerateFiles(
                         cloudSstDirectory,
                         "*.sst",
                         SearchOption.TopDirectoryOnly))
            {
                CopyIfPresent(cloudSst, Path.Combine(localSstDirectory, Path.GetFileName(cloudSst)));
            }
        }

        WalPublicationCatalog? catalog = LoadCatalog(cloudRoot);
        if (catalog is null)
        {
            return 0;
        }

        string localWalDirectory = Path.Combine(root, "wal");
        Directory.CreateDirectory(localWalDirectory);
        foreach ((ulong segmentId, PublishedWalSegment publication) in catalog.Segments)
        {
            ValidatePublication(segmentId, publication, catalog.FencingEpoch);
            string remotePath = ResolveObjectPath(cloudRoot, publication.ObjectKey);
            if (!File.Exists(remotePath))
            {
                throw PantsException.Create(
                    PantsErrorCode.RecoveryFailed,
                    $"Published cloud WAL object '{publication.ObjectKey}' is missing.");
            }

            byte[] bytes = File.ReadAllBytes(remotePath);
            if (checked((ulong)bytes.Length) != publication.SizeBytes ||
                MidgeDiskFormat.Crc32C(bytes) != publication.ContentCrc32C)
            {
                throw PantsException.Create(
                    PantsErrorCode.Corruption,
                    $"Published cloud WAL object '{publication.ObjectKey}' failed catalog validation.");
            }

            string localName = $"{segmentId:00000000000000000000}.wal";
            AtomicWrite(Path.Combine(localWalDirectory, localName), bytes);
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

        string objectKey = PantsCloudObjectLayout.WalSegmentObjectKey(
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

        if (_catalog.Segments.TryGetValue(segment.SegmentId, out PublishedWalSegment? existing))
        {
            if (!existing.Equals(publication))
            {
                throw PantsException.Create(
                    PantsErrorCode.Fenced,
                    $"Cloud WAL segment {segment.SegmentId} conflicts with its publication catalog entry.");
            }

            return;
        }

        AtomicWrite(ResolveObjectPath(_cloudRoot, objectKey), segment.Bytes);
        _catalog.Segments.Add(segment.SegmentId, publication);
        SaveCatalog();
        MirrorMetadataAndSsts();
    }

    public void MirrorMetadataAndSsts()
    {
        foreach (string fileName in MetadataFiles)
        {
            string localPath = Path.Combine(_localRoot, fileName);
            if (File.Exists(localPath))
            {
                AtomicWrite(
                    Path.Combine(_cloudRoot, "metadata", fileName),
                    File.ReadAllBytes(localPath));
            }
        }

        string localSstDirectory = Path.Combine(_localRoot, "sst");
        if (!Directory.Exists(localSstDirectory))
        {
            return;
        }

        foreach (string localSst in Directory.EnumerateFiles(
                     localSstDirectory,
                     "*.sst",
                     SearchOption.TopDirectoryOnly))
        {
            AtomicWrite(
                Path.Combine(_cloudRoot, "sst", Path.GetFileName(localSst)),
                File.ReadAllBytes(localSst));
        }
    }

    public void PublishColumnFamilyCreate(MidgeColumnFamilyMeta metadata)
    {
        DdlRegistry registry = LoadDdlRegistry();
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
        DdlRegistry registry = LoadDdlRegistry();
        int index = registry.ColumnFamilies.FindIndex(family => family.Id == metadata.Id);
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

    private static WalPublicationCatalog? LoadCatalog(string cloudRoot)
    {
        string path = Path.Combine(cloudRoot, "wal", "publication-catalog.v1.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            WalPublicationCatalog catalog = JsonSerializer.Deserialize<WalPublicationCatalog>(
                File.ReadAllBytes(path),
                JsonOptions) ?? throw new JsonException("Cloud WAL catalog is empty.");
            if (catalog.FormatVersion != CatalogFormatVersion || catalog.FencingEpoch == 0)
            {
                throw new JsonException("Cloud WAL catalog header is invalid.");
            }

            foreach ((ulong segmentId, PublishedWalSegment publication) in catalog.Segments)
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

    private static void ValidatePublication(
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

        string expectedKey = PantsCloudObjectLayout.WalSegmentObjectKey(
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

    private void SaveCatalog() => AtomicWrite(
        Path.Combine(_cloudRoot, "wal", "publication-catalog.v1.json"),
        JsonSerializer.SerializeToUtf8Bytes(_catalog, JsonOptions));

    private void EnsureDdlRegistry()
    {
        string path = ResolveObjectPath(_cloudRoot, PantsCloudObjectLayout.DdlRegistryObjectKey);
        if (!File.Exists(path))
        {
            AtomicWrite(path, "{\n  \"epoch\": 0,\n  \"column_families\": [],\n  \"operations\": []\n}"u8.ToArray());
        }
    }

    private DdlRegistry LoadDdlRegistry()
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

    private void SaveDdlRegistry(DdlRegistry registry) => AtomicWrite(
        ResolveObjectPath(_cloudRoot, PantsCloudObjectLayout.DdlRegistryObjectKey),
        JsonSerializer.SerializeToUtf8Bytes(registry, JsonOptions));

    private static void CopyIfPresent(string source, string destination)
    {
        if (File.Exists(source) && !File.Exists(destination))
        {
            AtomicWrite(destination, File.ReadAllBytes(source));
        }
    }

    private static string ResolveObjectPath(string cloudRoot, string objectKey)
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

    private static void AtomicWrite(string path, byte[] bytes)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        using (var stream = new FileStream(
                   temporary,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private sealed class WalPublicationCatalog
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

    private sealed class PublishedWalSegment : IEquatable<PublishedWalSegment>
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

    private sealed class DdlRegistry
    {
        public ulong Epoch { get; set; }

        public List<MidgeColumnFamilyMeta> ColumnFamilies { get; set; } = [];

        public List<DdlOperation> Operations { get; set; } = [];
    }

    private sealed class DdlOperation
    {
        [JsonPropertyName("op_id")]
        public string OperationId { get; set; } = string.Empty;

        public Dictionary<string, object> Edit { get; set; } = [];
    }
}
