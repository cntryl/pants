using System.Text.Json;

namespace Cntryl.Pants.Storage.Internal.Manifest;

static class ManifestCodec
{
    public static byte[] Encode(ManifestState manifest, JsonSerializerOptions options)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("files");
            JsonSerializer.Serialize(writer, manifest.Files, options);
            writer.WritePropertyName("column_families");
            JsonSerializer.Serialize(writer, manifest.ColumnFamilies, options);
            writer.WritePropertyName("next_sst_seqs");
            JsonSerializer.Serialize(writer, manifest.NextSstSeqs, options);
            writer.WriteNumber("edit_checkpoint_id", manifest.EditCheckpointId);
            writer.WritePropertyName("flushed_through");
            new FlushFrontierMapConverter().Write(writer, manifest.FlushedThrough, options);
            if (manifest.RetiredSsts.Count != 0)
            {
                writer.WritePropertyName("retired_ssts");
                JsonSerializer.Serialize(writer, manifest.RetiredSsts, options);
            }

            writer.WriteEndObject();
        }

        return MetadataFrameCodec.Encode(1, stream.ToArray());
    }

    public static ManifestState Decode(ReadOnlySpan<byte> bytes)
    {
        using var document = MetadataFrameCodec.Decode(bytes, 1);
        return Decode(document.RootElement, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
    }

    public static ManifestState Decode(JsonElement root, JsonSerializerOptions options)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("flushed_through", out _))
        {
            throw new PantsCorruptionException("FORMAT 9 manifest is missing flush authority.");
        }

        if (root.TryGetProperty("column_families", out var families) && families.ValueKind == JsonValueKind.Array)
        {
            foreach (var family in families.EnumerateArray())
            {
                if (family.ValueKind != JsonValueKind.Object ||
                    !family.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetUInt32(out _) ||
                    !family.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                {
                    throw new PantsCorruptionException("Manifest column-family identity is missing or malformed.");
                }
            }
        }

        var manifest = root.Deserialize<ManifestState>(options) ?? throw new PantsCorruptionException("Manifest is empty.");
        if (manifest.Files is null || manifest.ColumnFamilies is null || manifest.NextSstSeqs is null ||
            manifest.RetiredSsts is null || manifest.FlushedThrough is null ||
            manifest.Files.Any(static file => file is null) || manifest.ColumnFamilies.Any(static family => family is null) ||
            manifest.RetiredSsts.Values.Any(static file => file is null))
        {
            throw new PantsCorruptionException("Manifest authority contains a null collection or identity.");
        }

        var liveNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var family in manifest.ColumnFamilies)
        {
            if (family.Name is null or "default" || family.DroppedSstNames is null ||
                family.Reclaimed && family.DeletedAt is null ||
                family.DeletedAt.HasValue && !family.DropSequence.HasValue ||
                !family.DeletedAt.HasValue && (family.DropSequence.HasValue || family.DroppedSstNames.Count != 0) ||
                family.DeletedAt is null && !liveNames.Add(family.Name) ||
                family.DroppedSstNames.Any(static name => !SstFileName.IsSafe(name)))
            {
                throw new PantsCorruptionException("Manifest column-family lifecycle is inconsistent.");
            }
        }

        if (manifest.ColumnFamilies.Any(static family => family.Id == 0) ||
            manifest.ColumnFamilies.Select(static family => family.Id).Distinct().Count() != manifest.ColumnFamilies.Count ||
            manifest.Files.Select(static file => file.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Files.Count ||
            manifest.Files.Any(file => !SstFileName.IsSafe(file.Name) || file.ColumnFamilyId != 0 && !manifest.ColumnFamilies.Any(family => family.Id == file.ColumnFamilyId)) ||
            manifest.NextSstSeqs.Keys.Any(id => !manifest.FlushedThrough.ContainsKey(id)) ||
            manifest.FlushedThrough.Count != manifest.ColumnFamilies.Count + 1 ||
            manifest.ColumnFamilies.Any(family => !manifest.FlushedThrough.TryGetValue(family.Id, out var frontier) || family.DeletedAt.HasValue != (frontier == ulong.MaxValue)))
        {
            throw new PantsCorruptionException("Manifest file or column-family flush authority is inconsistent.");
        }

        foreach (var pair in manifest.RetiredSsts)
        {
            if (pair.Key != pair.Value.Name || !SstFileName.IsSafe(pair.Key) || manifest.Files.Any(file => file.Name == pair.Key) ||
                !manifest.FlushedThrough.ContainsKey(pair.Value.ColumnFamilyId))
            {
                throw new PantsCorruptionException("Retired SST identity conflicts with manifest authority.");
            }
        }

        manifest.LastPersistedSequence = new[]
        {
            manifest.FlushedThrough.Values.Where(static sequence => sequence != ulong.MaxValue).DefaultIfEmpty().Max(),
            manifest.Files.Select(static file => file.LargestSequence ?? file.SmallestSequence ?? 0).DefaultIfEmpty().Max(),
            manifest.ColumnFamilies.Select(static family => family.DropSequence ?? 0).DefaultIfEmpty().Max()
        }.Max();
        return manifest;
    }
}
