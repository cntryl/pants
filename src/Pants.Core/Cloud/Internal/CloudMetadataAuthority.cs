using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cntryl.Pants.Storage.Internal;

namespace Cntryl.Pants.Cloud.Internal;

/// <summary>Publishes one exact immutable metadata presence set through a fenced pointer.</summary>
sealed class CloudMetadataAuthority : IDisposable
{
    internal const string PointerKey = "metadata/committed-pointer.v1";
    internal const string SentinelKey = "metadata/authority-initialized.v1";
    internal static readonly string[] FileNames = ["FORMAT", "manifest.snapshot.json", "manifest.journal", "intent_log.json"];
    static readonly byte[] Pending = "midge cloud metadata authority v3 pending\n"u8.ToArray();
    static readonly byte[] Initialized = "midge cloud metadata authority v3 initialized\n"u8.ToArray();
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    readonly SemaphoreSlim _gate = new(1, 1);

    readonly ICloudObjectStore _store;
    readonly ResourceBudget _budget;
    ICloudObjectStore Store => _store;
    readonly ulong _epoch;
    readonly string _holderId;
    readonly string _ownerToken;
    readonly Action _ensureValid;
    readonly Func<CancellationToken, ValueTask> _validateRemote;

    public CloudMetadataAuthority(ICloudObjectStore store, CloudLeaseCoordinator lease, ResourceBudget? budget = null)
        : this(store, lease.Epoch, lease.HolderId, lease.OwnerToken, lease.EnsureValid, lease.ValidateRemoteAsync, budget)
    {
    }

    internal CloudMetadataAuthority(ICloudObjectStore store, ulong epoch, string holderId, string ownerToken,
        Action ensureValid, Func<CancellationToken, ValueTask> validateRemote, ResourceBudget? budget = null)
    {
        _store = store;
        _budget = budget ?? new ResourceBudget(64L * 1024 * 1024);
        _epoch = epoch;
        _holderId = holderId;
        _ownerToken = ownerToken;
        _ensureValid = ensureValid;
        _validateRemote = validateRemote;
    }

    public void Dispose() => _gate.Dispose();

    public async ValueTask ClaimAsync(CancellationToken cancellationToken)
    {
        using var reads = new ProviderCloudControlMetadataReadSet(Store, _budget);
        var sentinel = await ReadBoundedControlAsync(Store, reads, SentinelKey, 128, cancellationToken).ConfigureAwait(false);
        var pointer = await ReadBoundedControlAsync(Store, reads, PointerKey, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        if (sentinel is null)
        {
            if (pointer is not null)
            {
                throw new PantsCorruptionException("The committed metadata pointer has no initialization sentinel.");
            }

            foreach (var name in FileNames)
            {
                if (await Store.HeadAsync("metadata/" + name, cancellationToken).ConfigureAwait(false) is not null)
                {
                    throw new PantsCompatibilityException("Legacy cloud metadata requires logical export/import into a fresh prefix.");
                }
            }

            if (!await Store.PutAsync(SentinelKey, Pending, new PantsCloudObjectWriteCondition.IfAbsent(), cancellationToken).ConfigureAwait(false))
            {
                throw new PantsFencedException("Cloud metadata initialization raced another writer.");
            }

            sentinel = await ReadBoundedControlAsync(Store, reads, SentinelKey, 128, cancellationToken).ConfigureAwait(false) ??
                       throw new PantsLeaseIndeterminateException("Cloud metadata initialization was not confirmed.");
        }

        var pending = sentinel.Data.Span.SequenceEqual(Pending);
        if (!pending && !sentinel.Data.Span.SequenceEqual(Initialized))
        {
            throw new PantsCompatibilityException("Cloud metadata requires initialization sentinel version 3.");
        }

        if (pointer is null && !pending)
        {
            throw new PantsCorruptionException("Initialized cloud metadata has lost its committed pointer.");
        }

        var current = pointer is null ? null : DecodePointer(pointer.Data.Span);
        if (current is not null && (current.WriterEpoch > _epoch ||
            current.WriterEpoch == _epoch && (current.HolderId != _holderId || current.OwnerToken != _ownerToken)))
        {
            throw new PantsFencedException("The committed metadata pointer belongs to another writer.");
        }

        var claimed = new Pointer(_epoch, _holderId, _ownerToken, current?.Committed);
        using var encoded = new CloudMetadataWriteAdmission(_budget).Serialize(claimed, Options, "Cloud metadata pointer claim");
        var bytes = MetadataFrameCodec.Encode(4, encoded.Bytes.Span);
        await _validateRemote(cancellationToken).ConfigureAwait(false);
        var written = await PutPointerAsync(bytes,
            pointer is null ? new PantsCloudObjectWriteCondition.IfAbsent() : new PantsCloudObjectWriteCondition.IfVersion(pointer.Version), cancellationToken).ConfigureAwait(false);
        if (!written)
        {
            throw new PantsFencedException("The cloud metadata pointer claim was fenced.");
        }

        await VerifyAsync(PointerKey, bytes, cancellationToken).ConfigureAwait(false);
        if (pending)
        {
            if (!await Store.PutAsync(SentinelKey, Initialized, new PantsCloudObjectWriteCondition.IfVersion(sentinel.Version), cancellationToken).ConfigureAwait(false))
            {
                throw new PantsFencedException("Cloud metadata initialization changed during pointer claim.");
            }
        }
    }

    public async ValueTask PublishAsync(CloudControlMetadataSnapshot metadata, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var reads = new ProviderCloudControlMetadataReadSet(Store, _budget);
            await _validateRemote(cancellationToken).ConfigureAwait(false);
            ValidateFiles(metadata.Files, metadata.MaximumManifestSequence);
            var pointerObject = await ReadBoundedControlAsync(Store, reads, PointerKey, 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (pointerObject is null)
            {
                await ClaimAsync(cancellationToken).ConfigureAwait(false);
                pointerObject = await ReadBoundedControlAsync(Store, reads, PointerKey, 1024 * 1024, cancellationToken).ConfigureAwait(false) ??
                                throw new PantsCorruptionException("Cloud metadata pointer is missing before publication.");
            }
            var pointer = DecodePointer(pointerObject.Data.Span);
            EnsureOwner(pointer);
            var predecessorFiles = pointer.Committed is null ? null :
                await ReadFilesAsync(Store, reads, pointer.Committed, cancellationToken).ConfigureAwait(false);
            if (predecessorFiles is not null && FileNames.All(name =>
                    predecessorFiles.TryGetValue(name, out var previous) == metadata.Files.TryGetValue(name, out var next) &&
                    (!predecessorFiles.ContainsKey(name) || previous.Span.SequenceEqual(next.Span))))
            {
                return;
            }

            var objects = new List<MetadataObject>();
            var generation = Guid.NewGuid().ToString("D");
            foreach (var name in FileNames)
            {
                if (!metadata.Files.TryGetValue(name, out var bytes))
                {
                    continue;
                }

                using var publicationReservation = new CloudMetadataWriteAdmission(_budget).AdmitExisting(bytes.Length, "Cloud immutable metadata publication");
                var key = $"metadata/generations/{generation}/{name}";
                if (!await Store.PutAsync(key, bytes, new PantsCloudObjectWriteCondition.IfAbsent(), cancellationToken).ConfigureAwait(false))
                {
                    throw new PantsCorruptionException("An immutable metadata upload already exists.");
                }

                await VerifyAsync(key, bytes, cancellationToken).ConfigureAwait(false);
                objects.Add(new MetadataObject(name, key, checked((ulong)bytes.Length), DiskFormat.Crc32C(bytes.Span)));
            }

            if (!objects.Any(value => value.FileName == "FORMAT") || !objects.Any(value => value.FileName == "manifest.snapshot.json"))
            {
                throw new PantsCorruptionException("A cloud metadata generation must contain FORMAT and a manifest snapshot.");
            }

            var nextGeneration = new Generation(checked((pointer.Committed?.Id ?? 0) + 1), metadata.MaximumManifestSequence, objects.ToArray());
            if (pointer.Committed is not null && nextGeneration.ManifestHorizon < pointer.Committed.ManifestHorizon)
            {
                throw new PantsFencedException("Cloud metadata publication would regress the manifest horizon.");
            }

            using var encoded = new CloudMetadataWriteAdmission(_budget).Serialize(pointer with { Committed = nextGeneration }, Options, "Cloud metadata pointer publication");
            var proposed = MetadataFrameCodec.Encode(4, encoded.Bytes.Span);
            await _validateRemote(cancellationToken).ConfigureAwait(false);
            if (!await PutPointerAsync(proposed, new PantsCloudObjectWriteCondition.IfVersion(pointerObject.Version), cancellationToken).ConfigureAwait(false))
            {
                throw new PantsFencedException("Cloud metadata publication lost its committed pointer condition.");
            }

            await VerifyAsync(PointerKey, proposed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static ulong ReadGenerationOrdinal(ReadOnlySpan<byte> bytes) => DecodePointer(bytes).Committed?.Id ?? 0;

    public static async ValueTask<Dictionary<string, ReadOnlyMemory<byte>>?> ReadCommittedFilesAsync(
        ICloudObjectStore store, CancellationToken cancellationToken, ResourceBudget? budget = null,
        ProviderCloudControlMetadataReadSet? readSet = null, ulong minimumGeneration = 0)
    {
        using var ownedReads = readSet is null ? new ProviderCloudControlMetadataReadSet(store, budget ?? new ResourceBudget(64L * 1024 * 1024)) : null;
        var reads = readSet ?? ownedReads!;
        try
        {
            return await ReadCommittedFilesCoreAsync(store, reads, minimumGeneration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or JsonException)
        {
            throw new PantsCorruptionException("Committed cloud metadata has an invalid structure.", exception);
        }
    }

    static async ValueTask<Dictionary<string, ReadOnlyMemory<byte>>?> ReadCommittedFilesCoreAsync(
        ICloudObjectStore store, ProviderCloudControlMetadataReadSet reads, ulong minimumGeneration,
        CancellationToken cancellationToken)
    {
        var sentinel = await ReadBoundedControlAsync(store, reads, SentinelKey, 128, cancellationToken).ConfigureAwait(false);
        var pointer = await ReadBoundedControlAsync(store, reads, PointerKey, 1024 * 1024, cancellationToken).ConfigureAwait(false);
        if (pointer is null)
        {
            if (sentinel is not null)
            {
                throw new PantsCorruptionException("Cloud metadata has no committed pointer.");
            }

            foreach (var name in FileNames)
            {
                if (await store.HeadAsync("metadata/" + name, cancellationToken).ConfigureAwait(false) is not null)
                {
                    throw new PantsCompatibilityException("Mutable cloud metadata is not recovery authority in FORMAT 9.");
                }
            }

            if (minimumGeneration != 0)
            {
                throw new PantsRecoveryFailedException("Retired cloud WAL has no committed shared metadata generation.");
            }

            return null;
        }

        if (sentinel is null || !sentinel.Data.Span.SequenceEqual(Initialized))
        {
            throw new PantsCorruptionException("Cloud metadata initialization is incomplete.");
        }

        var decoded = DecodePointer(pointer.Data.Span);
        if ((decoded.Committed?.Id ?? 0) < minimumGeneration)
        {
            throw new PantsRecoveryFailedException(
                $"Committed cloud metadata generation {decoded.Committed?.Id ?? 0} is older than " +
                $"WAL retirement generation {minimumGeneration}.");
        }

        return decoded.Committed is null ? new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal) :
            await ReadFilesAsync(store, reads, decoded.Committed, cancellationToken).ConfigureAwait(false);
    }

    static async ValueTask<Dictionary<string, ReadOnlyMemory<byte>>> ReadFilesAsync(
        ICloudObjectStore store, ProviderCloudControlMetadataReadSet reads, Generation generation, CancellationToken cancellationToken)
    {
        if (generation.Id == 0 || generation.Objects is null || generation.Objects.Length is < 1 or > 16)
        {
            throw new PantsCorruptionException("Cloud metadata generation has invalid identity or closure bounds.");
        }

        if (generation.Objects[0].FileName == "manifest-chain")
        {
            return await ReadChainAsync(store, reads, generation, cancellationToken).ConfigureAwait(false);
        }

        if (generation.Objects.Length > 4)
        {
            throw new PantsCorruptionException("Cloud metadata per-file generation has too many objects.");
        }

        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        ulong totalBytes = 0;
        foreach (var item in generation.Objects)
        {
            totalBytes = checked(totalBytes + item.Len);
            if (totalBytes > 64UL * 1024 * 1024)
            {
                throw new PantsResourceLimitException("Cloud metadata generation exceeds its closure byte bound.");
            }

            var parts = item.ObjectKey.Split('/');
            if (!FileNames.Contains(item.FileName, StringComparer.Ordinal) || parts.Length != 4 ||
                parts[0] != "metadata" || parts[1] != "generations" || parts[3] != item.FileName ||
                !IsCanonicalUuid(parts[2]) || files.ContainsKey(item.FileName))
            {
                throw new PantsCorruptionException("Cloud metadata contains an invalid immutable per-file reference.");
            }

            files.Add(item.FileName, await ReadObjectAsync(store, reads, item, cancellationToken).ConfigureAwait(false));
        }

        ValidateFiles(files, generation.ManifestHorizon);
        return files;
    }

    static async ValueTask<Dictionary<string, ReadOnlyMemory<byte>>> ReadChainAsync(
        ICloudObjectStore store, ProviderCloudControlMetadataReadSet reads, Generation generation, CancellationToken cancellationToken)
    {
        if (generation.Id < checked((ulong)generation.Objects.Length))
        {
            throw new PantsCorruptionException("Cloud manifest closure extends before the first generation.");
        }

        var versions = new List<JsonDocument>();
        try
        {
            ulong total = 0;
            ulong previousEpoch = ulong.MaxValue;
            for (var index = 0; index < generation.Objects.Length; index++)
            {
                var reference = generation.Objects[index];
                total = checked(total + reference.Len);
                var parts = reference.ObjectKey.Split('/');
                if (total > 64UL * 1024 * 1024 || reference.FileName != "manifest-chain" || parts.Length != 3 ||
                    parts[0] != "manifest" || parts[1] != (generation.Id - checked((ulong)index)).ToString("D20", CultureInfo.InvariantCulture) || !IsCanonicalUuid(parts[2]))
                {
                    throw new PantsCorruptionException("Cloud manifest closure has invalid bounds or object identity.");
                }

                var bytes = await ReadObjectAsync(store, reads, reference, cancellationToken).ConfigureAwait(false);
                var document = MetadataFrameCodec.Decode(bytes.Span, 5);
                versions.Add(document);
                var value = document.RootElement;
                RequireFields(value, ["id", "writer_epoch", "operation_id", "parent", "changes"], ["cut"]);
                var epoch = value.GetProperty("writer_epoch").GetUInt64();
                if (value.GetProperty("id").GetUInt64() != generation.Id - checked((ulong)index) || epoch == 0 || epoch > previousEpoch ||
                    value.GetProperty("operation_id").GetString() != parts[2])
                {
                    throw new PantsCorruptionException("Cloud manifest version does not match its closure identity.");
                }

                previousEpoch = epoch;
                var parent = value.GetProperty("parent");
                if (index + 1 == generation.Objects.Length ? parent.ValueKind != JsonValueKind.Null :
                    parent.ValueKind == JsonValueKind.Null || DecodeReference(parent) != generation.Objects[index + 1])
                {
                    throw new PantsCorruptionException("Cloud manifest closure parent does not match the committed pointer.");
                }
            }

            var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
            for (var index = versions.Count - 1; index >= 0; index--)
            {
                var changes = versions[index].RootElement.GetProperty("changes");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var edit in changes.EnumerateObject())
                {
                    if (!FileNames.Contains(edit.Name, StringComparer.Ordinal) || !names.Add(edit.Name))
                    {
                        throw new PantsCorruptionException("Cloud manifest changes contain an unknown or duplicate file.");
                    }

                    if (edit.Value.ValueKind == JsonValueKind.String && edit.Value.GetString() == "Remove")
                    {
                        if (index == versions.Count - 1)
                        {
                            throw new PantsCorruptionException("A parentless manifest version must contain only replacements.");
                        }

                        files.Remove(edit.Name);
                        continue;
                    }

                    var fields = edit.Value.EnumerateObject().ToArray();
                    if (fields.Length != 1)
                    {
                        throw new PantsCorruptionException("Cloud manifest file edit must contain exactly one operation.");
                    }

                    var operation = fields[0];
                    if (operation.Name == "Replace")
                    {
                        files[edit.Name] = DecodeBytes(operation.Value);
                    }
                    else if (operation.Name == "Suffix" && index != versions.Count - 1 && files.TryGetValue(edit.Name, out var previous))
                    {
                        RequireFields(operation.Value, ["keep", "bytes"]);
                        var keep = operation.Value.GetProperty("keep").GetUInt64();
                        var suffix = DecodeBytes(operation.Value.GetProperty("bytes"));
                        if (keep > checked((ulong)previous.Length) || keep + checked((ulong)suffix.Length) > 64UL * 1024 * 1024)
                        {
                            throw new PantsCorruptionException("Cloud manifest suffix has invalid prefix bounds.");
                        }

                        var reconstructed = new byte[checked((int)keep + suffix.Length)];
                        previous.Span[..checked((int)keep)].CopyTo(reconstructed);
                        suffix.CopyTo(reconstructed.AsSpan(checked((int)keep)));
                        files[edit.Name] = reconstructed;
                    }
                    else
                    {
                        throw new PantsCorruptionException("Cloud manifest file edit cannot be applied.");
                    }
                }

                if (versions[index].RootElement.TryGetProperty("cut", out var cut))
                {
                    ValidateCut(cut, files, versions[index].RootElement.GetProperty("writer_epoch").GetUInt64());
                }
            }

            ValidateFiles(files, generation.ManifestHorizon);
            return files;
        }
        finally
        {
            foreach (var version in versions)
            {
                version.Dispose();
            }
        }
    }

    static void ValidateCut(JsonElement cut, Dictionary<string, ReadOnlyMemory<byte>> files, ulong epoch)
    {
        RequireFields(cut, ["writer_epoch", "session", "ordinal", "digest", "covers"]);
        var ordinal = cut.GetProperty("ordinal").GetUInt64();
        var covers = cut.GetProperty("covers");
        if (cut.GetProperty("writer_epoch").GetUInt64() != epoch || ordinal == 0 || !IsCanonicalUuid(cut.GetProperty("session").GetString() ?? "") || covers.GetArrayLength() > 63)
        {
            throw new PantsCorruptionException("Cloud manifest metadata cut identity is invalid.");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        foreach (var name in FileNames)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(length, checked((ulong)nameBytes.Length));
            hash.AppendData(length);
            hash.AppendData(nameBytes);
            var present = files.TryGetValue(name, out var bytes);
            hash.AppendData(present ? [1] : [0]);
            if (present)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(length, checked((ulong)bytes.Length));
                hash.AppendData(length);
                hash.AppendData(bytes.Span);
            }
        }

        if (!hash.GetHashAndReset().AsSpan().SequenceEqual(DecodeBytes(cut.GetProperty("digest"))))
        {
            throw new PantsCorruptionException("Cloud manifest metadata cut digest does not match its exact files.");
        }

        ulong previous = 0;
        foreach (var witness in covers.EnumerateArray())
        {
            RequireFields(witness, ["ordinal", "digest"]);
            var next = witness.GetProperty("ordinal").GetUInt64();
            if (next <= previous || next >= ordinal || DecodeBytes(witness.GetProperty("digest")).Length != 32)
            {
                throw new PantsCorruptionException("Cloud manifest metadata cut witnesses are invalid.");
            }

            previous = next;
        }
    }

    static void ValidateFiles(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, ulong horizon)
    {
        if (!files.TryGetValue("FORMAT", out var format) || !LocalDiskStore.IsSupportedFormatMarker(format.Span) ||
            !files.ContainsKey("manifest.snapshot.json"))
        {
            throw new PantsCorruptionException("Committed cloud metadata lacks its required FORMAT or snapshot.");
        }

        var decoded = CloudManifestReader.DecodeMetadata(files);
        if (files.TryGetValue("intent_log.json", out var intent))
        {
            var parsed = IntentLogCodec.Read(intent.ToArray());
            if (parsed.ValidLength != intent.Length)
            {
                throw new PantsCorruptionException("Committed cloud intent bytes have an incomplete suffix.");
            }
        }

        if (decoded.EditCheckpointId != horizon)
        {
            throw new PantsCorruptionException("Cloud metadata horizon precedes its manifest snapshot.");
        }
    }

    static async ValueTask<CloudObject?> ReadBoundedControlAsync(ICloudObjectStore store, ProviderCloudControlMetadataReadSet reads, string key, ulong maximumBytes, CancellationToken cancellationToken)
    {
        var head = await store.HeadAsync(key, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            return null;
        }

        if (head.SizeBytes > maximumBytes)
        {
            throw new PantsResourceLimitException("Cloud metadata authority object exceeds its admission bound.");
        }

        var value = await reads.ReadAsync(key, cancellationToken).ConfigureAwait(false) ??
                    throw new PantsLeaseIndeterminateException("Cloud metadata authority object changed during read.");
        if (value.Version != head.Version || checked((ulong)value.Data.Length) != head.SizeBytes)
        {
            throw new PantsLeaseIndeterminateException("Cloud metadata authority object changed during read.");
        }

        return value;
    }

    static async ValueTask<ReadOnlyMemory<byte>> ReadObjectAsync(ICloudObjectStore store, ProviderCloudControlMetadataReadSet reads, MetadataObject reference, CancellationToken cancellationToken)
    {
        if (reference.Len > 64UL * 1024 * 1024)
        {
            throw new PantsResourceLimitException("Cloud metadata object exceeds its admission bound.");
        }

        var head = await store.HeadAsync(reference.ObjectKey, cancellationToken).ConfigureAwait(false) ??
                   throw new PantsCorruptionException("Committed cloud metadata object is missing.");
        if (head.SizeBytes != reference.Len)
        {
            throw new PantsCorruptionException("Committed cloud metadata object length differs from its pointer.");
        }

        var value = await reads.ReadAsync(reference.ObjectKey, cancellationToken).ConfigureAwait(false) ??
                    throw new PantsCorruptionException("Committed cloud metadata object disappeared.");
        if (value.Version != head.Version || checked((ulong)value.Data.Length) != reference.Len || DiskFormat.Crc32C(value.Data.Span) != reference.Crc32C)
        {
            throw new PantsCorruptionException("Committed cloud metadata object identity or checksum differs from its pointer.");
        }

        return value.Data;
    }

    async ValueTask<bool> PutPointerAsync(ReadOnlyMemory<byte> bytes, CloudObjectWriteCondition condition, CancellationToken cancellationToken)
    {
        try
        {
            return await Store.PutAsync(PointerKey, bytes, condition, cancellationToken).ConfigureAwait(false);
        }
        catch (PantsException exception) when (exception is PantsIOException or PantsTimeoutException or PantsLeaseIndeterminateException)
        {
            // Only the exact same epoch, owner and descriptor can resolve a lost CAS response.
            await VerifyAsync(PointerKey, bytes, cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    async ValueTask VerifyAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        using var reads = new ProviderCloudControlMetadataReadSet(Store, _budget);
        var value = await reads.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        _ensureValid();
        if (value is null || !value.Data.Span.SequenceEqual(bytes.Span))
        {
            throw new PantsLeaseIndeterminateException("Cloud metadata mutation was not confirmed by exact readback.");
        }
    }

    void EnsureOwner(Pointer pointer)
    {
        _ensureValid();
        if (pointer.WriterEpoch != _epoch || pointer.HolderId != _holderId || pointer.OwnerToken != _ownerToken)
        {
            throw new PantsFencedException("Cloud metadata pointer is owned by another writer.");
        }
    }

    static Pointer DecodePointer(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return DecodePointerCore(bytes);
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or JsonException)
        {
            throw new PantsCorruptionException("Cloud metadata pointer has an invalid structure.", exception);
        }
    }

    static Pointer DecodePointerCore(ReadOnlySpan<byte> bytes)
    {
        using var document = MetadataFrameCodec.Decode(bytes, 4);
        var value = document.RootElement;
        RequireFields(value, ["writer_epoch", "holder_id", "owner_token", "committed"]);
        var epoch = value.GetProperty("writer_epoch").GetUInt64();
        var holder = value.GetProperty("holder_id").GetString() ?? "";
        var owner = value.GetProperty("owner_token").GetString() ?? "";
        if (epoch == 0 || holder.Length == 0 || owner.Length == 0)
        {
            throw new PantsCorruptionException("Cloud metadata pointer ownership is invalid.");
        }

        var committed = value.GetProperty("committed");
        Generation? generation = null;
        if (committed.ValueKind != JsonValueKind.Null)
        {
            RequireFields(committed, ["id", "manifest_horizon", "objects"]);
            generation = new Generation(committed.GetProperty("id").GetUInt64(), committed.GetProperty("manifest_horizon").GetUInt64(),
                committed.GetProperty("objects").EnumerateArray().Select(DecodeReference).ToArray());
        }

        return new Pointer(epoch, holder, owner, generation);
    }

    static MetadataObject DecodeReference(JsonElement value)
    {
        RequireFields(value, ["file_name", "object_key", "len", "crc32c"]);
        return new MetadataObject(value.GetProperty("file_name").GetString() ?? "", value.GetProperty("object_key").GetString() ?? "",
            value.GetProperty("len").GetUInt64(), value.GetProperty("crc32c").GetUInt32());
    }

    static void RequireFields(JsonElement value, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new PantsCorruptionException("Cloud metadata requires a map.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name) || !required.Contains(property.Name, StringComparer.Ordinal) && optional?.Contains(property.Name, StringComparer.Ordinal) != true)
            {
                throw new PantsCorruptionException("Cloud metadata has duplicate or unknown map fields.");
            }
        }

        if (required.Any(name => !names.Contains(name)))
        {
            throw new PantsCorruptionException("Cloud metadata is missing required map fields.");
        }
    }

    static bool IsCanonicalUuid(string value) => Guid.TryParseExact(value, "D", out var parsed) && parsed.ToString("D") == value;

    static byte[] DecodeBytes(JsonElement value) => value.EnumerateArray().Select(static element => element.GetByte()).ToArray();

    sealed record Pointer(ulong WriterEpoch, string HolderId, string OwnerToken, Generation? Committed);
    sealed record Generation(ulong Id, ulong ManifestHorizon, MetadataObject[] Objects);
    sealed record MetadataObject(string FileName, string ObjectKey, ulong Len, [property: System.Text.Json.Serialization.JsonPropertyName("crc32c")] uint Crc32C);
}
