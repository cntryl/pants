using System.Buffers.Binary;
using System.Text.Json;

namespace Cntryl.Pants.Storage.Internal.Recovery;

static class IntentLogCodec
{
    static readonly JsonSerializerOptions FileOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static IntentLogState Read(byte[] bytes)
    {
        var entries = new List<IntentEntry>();
        var cursor = 0;
        ulong sequence = 0;
        uint checksum = 0;
        while (cursor < bytes.Length)
        {
            if (!MetadataFrameCodec.TryGetFrameLength(bytes.AsSpan(cursor), 3, out var length))
            {
                break;
            }

            var frame = bytes.AsSpan(cursor, length);
            using var document = MetadataFrameCodec.Decode(frame, 3);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("sequence", out var sequenceValue) || !sequenceValue.TryGetUInt64(out var nextSequence) ||
                sequence == ulong.MaxValue || nextSequence != sequence + 1 ||
                !value.TryGetProperty("previous_checksum", out var checksumValue) || !checksumValue.TryGetUInt32(out var previousChecksum) || previousChecksum != checksum ||
                !value.TryGetProperty("keep", out var keepValue) || !keepValue.TryGetUInt64(out var keep) || keep > (ulong)entries.Count ||
                !value.TryGetProperty("append", out var append) || append.ValueKind != JsonValueKind.Array)
            {
                throw new PantsCorruptionException("Intent suffix chain is invalid.");
            }

            entries.RemoveRange((int)keep, entries.Count - (int)keep);
            foreach (var entry in append.EnumerateArray())
            {
                ValidateEntry(entry);
                entries.Add(IntentEntry.FromJson(JsonSerializer.SerializeToUtf8Bytes(entry)));
            }

            sequence = nextSequence;
            checksum = BinaryPrimitives.ReadUInt32BigEndian(frame[^4..]);
            cursor += length;
        }

        return new IntentLogState(entries, sequence, checksum, bytes, cursor);
    }

    public static byte[] CreateEdit(IntentLogState prior, List<IntentEntry> entries)
    {
        using var previous = ToDocument(prior.Entries);
        using var next = ToDocument(entries);
        var keep = 0;
        while (keep < prior.Entries.Count && keep < entries.Count && JsonElement.DeepEquals(previous.RootElement[keep], next.RootElement[keep]))
        {
            keep++;
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            sequence = checked(prior.Sequence + 1),
            previous_checksum = prior.Checksum,
            keep,
            append = next.RootElement.EnumerateArray().Skip(keep).ToArray()
        });
        return MetadataFrameCodec.Encode(3, payload);
    }

    public static JsonDocument ToDocument(List<IntentEntry> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var entry in entries)
            {
                entry.WriteTo(writer);
            }

            writer.WriteEndArray();
        }

        return JsonDocument.Parse(stream.ToArray());
    }

    static void ValidateEntry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || entry.EnumerateObject().Count() != 1)
        {
            throw new PantsCorruptionException("Intent entry must have exactly one variant.");
        }

        var variant = entry.EnumerateObject().Single();
        if (variant.Name is not ("SeqnoAllocated" or "FlushPlanned" or "CompactionPlanned" or "FlushPublish" or "CompactionPublish" or "MoveSstsPublish" or "SstAdded" or "CompactionApplied" or "WalSynced" or "CloudUploadComplete"))
        {
            throw new PantsCorruptionException("Intent entry variant is unknown.");
        }

        var fields = variant.Value;
        if (fields.ValueKind != JsonValueKind.Object)
        {
            throw new PantsCorruptionException("Intent variant fields must be a map.");
        }

        ValidateNames(fields);
        switch (variant.Name)
        {
            case "SeqnoAllocated":
                RequireUnsigned(fields, "seqno");
                RequireUnsigned(fields, "cf_id", uint.MaxValue);
                break;
            case "FlushPlanned":
                RequireUnsigned(fields, "cf_id", uint.MaxValue);
                var range = Require(fields, "seqno_range", JsonValueKind.Array);
                if (range.GetArrayLength() != 2 || range.EnumerateArray().Any(static item => !item.TryGetUInt64(out _)))
                {
                    throw new PantsCorruptionException("Flush sequence range is invalid.");
                }

                break;
            case "CompactionPlanned":
                Require(fields, "input_files", JsonValueKind.Array);
                RequireUnsigned(fields, "output_level", uint.MaxValue);
                break;
            case "FlushPublish":
                RequirePhase(fields);
                RequireUnsigned(fields, "cf_id", uint.MaxValue);
                RequireUnsigned(fields, "sequence");
                ValidateRuntimeFile(Require(fields, "file_meta", JsonValueKind.Object));
                break;
            case "CompactionPublish":
                RequirePhase(fields);
                RequireUnsigned(fields, "cf_id", uint.MaxValue);
                ValidateCompactionFiles(fields);
                break;
            case "CompactionApplied":
                ValidateCompactionFiles(fields);
                break;
            case "MoveSstsPublish":
                RequirePhase(fields);
                foreach (var file in Require(fields, "captured", JsonValueKind.Array).EnumerateArray())
                {
                    ValidateManifestFile(file);
                }

                RequireUnsigned(fields, "target_level", uint.MaxValue);
                break;
            case "SstAdded":
                ValidateRuntimeFile(Require(fields, "file_meta", JsonValueKind.Object));
                break;
            case "WalSynced":
                RequireUnsigned(fields, "segment_id");
                RequireUnsigned(fields, "seqno");
                break;
            case "CloudUploadComplete":
                Require(fields, "resource", JsonValueKind.String);
                RequireUnsigned(fields, "seqno");
                break;
        }

    }

    static void ValidateCompactionFiles(JsonElement fields)
    {
        Require(fields, "removed", JsonValueKind.Array);
        foreach (var file in Require(fields, "added", JsonValueKind.Array).EnumerateArray())
        {
            ValidateRuntimeFile(file);
        }
    }

    static void ValidateRuntimeFile(JsonElement file)
    {
        Require(file, "name", JsonValueKind.String);
        RequireUnsigned(file, "level", uint.MaxValue);
        RequireUnsigned(file, "size_bytes");
        RequireUnsigned(file, "cf_id", uint.MaxValue);
        foreach (var name in new[] { "smallest_key", "largest_key" })
        {
            if (!file.TryGetProperty(name, out var bound) || bound.ValueKind is not (JsonValueKind.Null or JsonValueKind.Array) ||
                bound.ValueKind == JsonValueKind.Array && bound.EnumerateArray().Any(static item => !item.TryGetByte(out _)))
            {
                throw new PantsCorruptionException("Runtime SST key bound must be a nullable byte array.");
            }
        }

        foreach (var name in new[] { "smallest_seq", "largest_seq" })
        {
            if (!file.TryGetProperty(name, out var sequence) || sequence.ValueKind != JsonValueKind.Null && !sequence.TryGetUInt64(out _))
            {
                throw new PantsCorruptionException("Runtime SST sequence bound must be nullable unsigned integer.");
            }
        }

        ValidateOptionalUnsigned(file, "content_crc32c", uint.MaxValue);
        ValidateOptionalUnsigned(file, "earliest_expiration_millis", ulong.MaxValue);
        if (file.TryGetProperty("key_bounds_complete", out var complete) && complete.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new PantsCorruptionException("Runtime SST bound completeness must be boolean.");
        }
    }

    static void ValidateManifestFile(JsonElement file)
    {
        try
        {
            if (file.ValueKind != JsonValueKind.Object || file.Deserialize<FileMeta>(FileOptions) is not { } metadata || !Manifest.SstFileName.IsSafe(metadata.Name))
            {
                throw new PantsCorruptionException("Captured SST identity is missing or malformed.");
            }
        }
        catch (JsonException exception)
        {
            throw new PantsCorruptionException("Captured SST identity is malformed.", exception);
        }
    }

    static void ValidateOptionalUnsigned(JsonElement fields, string name, ulong maximum)
    {
        if (fields.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null &&
            (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var number) || number > maximum))
        {
            throw new PantsCorruptionException($"Runtime SST field '{name}' is outside its unsigned range.");
        }
    }

    static void RequirePhase(JsonElement fields)
    {
        if (Require(fields, "phase", JsonValueKind.String).GetString() is not ("OutputDurable" or "ManifestPublished"))
        {
            throw new PantsCorruptionException("Intent publication phase is invalid.");
        }
    }

    static JsonElement Require(JsonElement fields, string name, JsonValueKind kind) =>
        fields.ValueKind == JsonValueKind.Object && fields.TryGetProperty(name, out var value) && value.ValueKind == kind
            ? value
            : throw new PantsCorruptionException($"Intent field '{name}' is missing or malformed.");

    static void RequireUnsigned(JsonElement fields, string name, ulong maximum = ulong.MaxValue)
    {
        var value = Require(fields, name, JsonValueKind.Number);
        if (!value.TryGetUInt64(out var number) || number > maximum)
        {
            throw new PantsCorruptionException($"Intent field '{name}' is outside its unsigned range.");
        }
    }

    static void ValidateNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name == "name" && property.Value.ValueKind == JsonValueKind.String && !Manifest.SstFileName.IsSafe(property.Value.GetString()!))
                {
                    throw new PantsCorruptionException($"Intent contains unsafe SST name '{property.Value.GetString()}'.");
                }

                if (property.Name is "removed" or "input_files" && property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var name in property.Value.EnumerateArray())
                    {
                        if (name.ValueKind != JsonValueKind.String || !Manifest.SstFileName.IsSafe(name.GetString()!))
                        {
                            throw new PantsCorruptionException($"Intent contains unsafe SST name '{(name.ValueKind == JsonValueKind.String ? name.GetString() : name.GetRawText())}'.");
                        }
                    }
                }

                ValidateNames(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                ValidateNames(item);
            }
        }
    }
}
