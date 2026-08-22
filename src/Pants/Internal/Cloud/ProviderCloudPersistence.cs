using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pants;

sealed class ProviderCloudPersistence : ICloudPersistence
{
    static readonly string[] MetadataFiles =
    [
        "FORMAT",
        "manifest.journal",
        "intent_log.json",
        "manifest.json",
        "manifest.snapshot.json"
    ];

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    readonly string _localRoot;
    readonly ICloudObjectStore _walStore;
    readonly ICloudObjectStore _sstStore;
    readonly ICloudObjectStore _controlStore;
    readonly CloudLeaseCoordinator _lease;
    readonly ulong _writerEpoch;

    public ProviderCloudPersistence(
        string localRoot,
        ICloudObjectStore walStore,
        ICloudObjectStore sstStore,
        ICloudObjectStore controlStore,
        CloudLeaseCoordinator lease)
    {
        _localRoot = Path.GetFullPath(localRoot);
        _walStore = walStore;
        _sstStore = sstStore;
        _controlStore = controlStore;
        _lease = lease;
        _writerEpoch = lease.Epoch;
    }

    public static async ValueTask HydrateLocalCacheAsync(
        string localRoot,
        ICloudObjectStore walStore,
        ICloudObjectStore sstStore,
        ICloudObjectStore controlStore,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(localRoot);
        Directory.CreateDirectory(root);
        foreach (var fileName in MetadataFiles)
        {
            var value = await controlStore.GetAsync(
                PantsCloudObjectLayout.MetadataPrefix + fileName,
                cancellationToken).ConfigureAwait(false);
            if (value is not null)
            {
                AtomicStagedFile.Write(Path.Combine(root, fileName), value.Data.Span);
            }
        }

        foreach (var sstName in ReadManifestSstNames(root))
        {
            var localPath = Path.Combine(root, "sst", sstName);
            if (File.Exists(localPath))
            {
                continue;
            }

            var remote = await sstStore.GetAsync(
                PantsCloudObjectLayout.SstPrefix + sstName,
                cancellationToken).ConfigureAwait(false) ??
                throw new PantsRecoveryFailedException(
                    $"Authoritative cloud SST '{sstName}' is missing.");
            AtomicStagedFile.Write(localPath, remote.Data.Span);
        }

        var catalogObject = await walStore.GetAsync(
            PantsCloudObjectLayout.WalCatalogObjectKey,
            cancellationToken).ConfigureAwait(false);
        if (catalogObject is null)
        {
            return;
        }

        var catalog = DecodeCatalog(catalogObject.Data.Span);
        foreach ((var segmentId, var segment) in catalog.Segments)
        {
            ValidateSegment(segmentId, segment, catalog.FencingEpoch);
            var remote = await walStore.GetAsync(segment.ObjectKey, cancellationToken)
                .ConfigureAwait(false) ?? throw new PantsRecoveryFailedException(
                    $"Published cloud WAL object '{segment.ObjectKey}' is missing.");
            if (checked((ulong)remote.Data.Length) != segment.SizeBytes ||
                MidgeDiskFormat.Crc32C(remote.Data.Span) != segment.ContentCrc32C)
            {
                throw new PantsCorruptionException(
                    $"Published cloud WAL object '{segment.ObjectKey}' failed catalog validation.");
            }

            AtomicStagedFile.Write(
                Path.Combine(root, "wal", $"{segmentId:00000000000000000000}.wal"),
                remote.Data.Span);
        }
    }

    public async ValueTask PublishWalAsync(
        SealedWalSegment segment,
        CancellationToken cancellationToken)
    {
        _lease.EnsureValid();
        if (segment.WriterEpoch != _writerEpoch)
        {
            throw new PantsFencedException("A WAL segment from a stale cloud lease cannot be published.");
        }

        var objectKey = PantsCloudObjectLayout.WalSegmentObjectKey(
            _writerEpoch,
            segment.SegmentId);
        var created = await _walStore.PutAsync(
            objectKey,
            segment.Bytes,
            new CloudObjectWriteCondition.IfAbsent(),
            cancellationToken).ConfigureAwait(false);
        if (!created)
        {
            var existing = await _walStore.GetAsync(objectKey, cancellationToken)
                .ConfigureAwait(false) ?? throw new PantsLeaseIndeterminateException(
                    "The immutable WAL upload outcome is indeterminate.");
            if (!existing.Data.Span.SequenceEqual(segment.Bytes))
            {
                throw new PantsFencedException("The cloud WAL object conflicts with this writer epoch.");
            }
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var current = await _walStore.GetAsync(
                PantsCloudObjectLayout.WalCatalogObjectKey,
                cancellationToken).ConfigureAwait(false);
            var catalog = current is null
                ? new ProviderWalCatalog { FencingEpoch = _writerEpoch }
                : DecodeCatalog(current.Data.Span);
            if (catalog.FencingEpoch > _writerEpoch)
            {
                throw new PantsFencedException("The cloud WAL catalog has a newer fencing epoch.");
            }

            var segments = new SortedDictionary<ulong, ProviderPublishedWalSegment>(catalog.Segments)
            {
                [segment.SegmentId] = new ProviderPublishedWalSegment
                {
                    SegmentId = segment.SegmentId,
                    WriterEpoch = _writerEpoch,
                    MaximumSequence = segment.MaximumSequence,
                    SizeBytes = checked((ulong)segment.Bytes.Length),
                    ContentCrc32C = MidgeDiskFormat.Crc32C(segment.Bytes),
                    ObjectKey = objectKey
                }
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                catalog with { FencingEpoch = _writerEpoch, Segments = segments },
                JsonOptions);
            var published = await _walStore.PutAsync(
                PantsCloudObjectLayout.WalCatalogObjectKey,
                bytes,
                current is null
                    ? new CloudObjectWriteCondition.IfAbsent()
                    : new CloudObjectWriteCondition.IfVersion(current.Version),
                cancellationToken).ConfigureAwait(false);
            if (published)
            {
                return;
            }
        }

        throw new PantsBusyException("Cloud WAL catalog publication exceeded its bounded CAS retries.");
    }

    public async ValueTask MirrorMetadataAndSstsAsync(CancellationToken cancellationToken)
    {
        _lease.EnsureValid();
        var sstDirectory = Path.Combine(_localRoot, "sst");
        if (Directory.Exists(sstDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(sstDirectory, "*.sst"))
            {
                var objectKey = PantsCloudObjectLayout.SstPrefix + Path.GetFileName(path);
                var created = await _sstStore.PutAsync(
                    objectKey,
                    File.ReadAllBytes(path),
                    new CloudObjectWriteCondition.IfAbsent(),
                    cancellationToken).ConfigureAwait(false);
                if (!created)
                {
                    var existing = await _sstStore.GetAsync(objectKey, cancellationToken)
                        .ConfigureAwait(false) ?? throw new PantsLeaseIndeterminateException(
                            $"Cloud SST upload outcome for '{objectKey}' is indeterminate.");
                    if (!existing.Data.Span.SequenceEqual(File.ReadAllBytes(path)))
                    {
                        throw new PantsFencedException($"Immutable cloud SST '{objectKey}' conflicts.");
                    }
                }
            }
        }

        foreach (var fileName in MetadataFiles)
        {
            var path = Path.Combine(_localRoot, fileName);
            if (File.Exists(path))
            {
                await PutControlCasAsync(
                    PantsCloudObjectLayout.MetadataPrefix + fileName,
                    File.ReadAllBytes(path),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask PublishColumnFamilyCreateAsync(
        MidgeColumnFamilyMeta metadata,
        CancellationToken cancellationToken) =>
        await PublishDdlAsync("CreateColumnFamily", metadata, cancellationToken).ConfigureAwait(false);

    public async ValueTask PublishColumnFamilyDropAsync(
        MidgeColumnFamilyMeta metadata,
        CancellationToken cancellationToken) =>
        await PublishDdlAsync("DropColumnFamily", metadata, cancellationToken).ConfigureAwait(false);

    async ValueTask PublishDdlAsync(
        string operation,
        MidgeColumnFamilyMeta metadata,
        CancellationToken cancellationToken)
    {
        _lease.EnsureValid();
        var current = await _controlStore.GetAsync(
            PantsCloudObjectLayout.DdlRegistryObjectKey,
            cancellationToken).ConfigureAwait(false);
        var document = new Dictionary<string, object?>
        {
            ["epoch"] = _writerEpoch,
            ["operation"] = operation,
            ["column_family"] = metadata
        };
        var published = await _controlStore.PutAsync(
            PantsCloudObjectLayout.DdlRegistryObjectKey,
            JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions),
            current is null
                ? new CloudObjectWriteCondition.IfAbsent()
                : new CloudObjectWriteCondition.IfVersion(current.Version),
            cancellationToken).ConfigureAwait(false);
        if (!published)
        {
            throw new PantsFencedException("Cloud DDL registry publication lost its authority race.");
        }
    }

    async ValueTask PutControlCasAsync(
        string objectKey,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        var current = await _controlStore.GetAsync(objectKey, cancellationToken)
            .ConfigureAwait(false);
        var published = await _controlStore.PutAsync(
            objectKey,
            data,
            current is null
                ? new CloudObjectWriteCondition.IfAbsent()
                : new CloudObjectWriteCondition.IfVersion(current.Version),
            cancellationToken).ConfigureAwait(false);
        if (!published)
        {
            throw new PantsFencedException(
                $"Cloud control object '{objectKey}' lost its conditional publication race.");
        }
    }

    static string[] ReadManifestSstNames(string root)
    {
        var path = File.Exists(Path.Combine(root, "manifest.snapshot.json"))
            ? Path.Combine(root, "manifest.snapshot.json")
            : Path.Combine(root, "manifest.json");
        if (!File.Exists(path))
        {
            return [];
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.TryGetProperty("files", out var files)
            ? files.EnumerateArray()
                .Select(file => file.GetProperty("name").GetString() ?? string.Empty)
                .Where(static name => name.Length > 0)
                .ToArray()
            : [];
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
        if (segment.SegmentId != segmentId || segment.WriterEpoch > fencingEpoch ||
            segment.SizeBytes == 0 || segment.ObjectKey != PantsCloudObjectLayout.WalSegmentObjectKey(
                segment.WriterEpoch,
                segmentId))
        {
            throw new PantsCorruptionException($"Cloud WAL catalog entry {segmentId} is invalid.");
        }
    }
}
