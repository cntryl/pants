using System.Text.Json;

namespace Cntryl.Pants.Support.TestDoubles;

static class CloudTestMetadata
{
    public static async Task SeedAsync(ICloudObjectStore store, ManifestState manifest, byte[]? manifestBytesOverride = null, byte[]? intentBytes = null)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        var manifestBytes = manifestBytesOverride ?? ManifestCodec.Encode(manifest, options);
        var objects = new List<object>();
        var objectGeneration = Guid.NewGuid().ToString("D");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["FORMAT"] = "midge-format-version=9\n"u8.ToArray(),
            ["manifest.snapshot.json"] = manifestBytes
        };
        if (intentBytes is not null)
        {
            files.Add("intent_log.json", intentBytes);
        }

        foreach (var file in files)
        {
            var key = $"metadata/generations/{objectGeneration}/" + file.Key;
            await store.PutAsync(key, file.Value, new PantsCloudObjectWriteCondition.Unconditional(), CancellationToken.None);
            objects.Add(new { file_name = file.Key, object_key = key, len = file.Value.Length, crc32c = DiskFormat.Crc32C(file.Value) });
        }

        var previous = await store.GetAsync(CloudMetadataAuthority.PointerKey, CancellationToken.None);
        var generation = previous is null ? 1UL : checked(CloudMetadataAuthority.ReadGenerationOrdinal(previous.Data.Span) + 1);
        var pointer = MetadataFrameCodec.Encode(4, JsonSerializer.SerializeToElement(new
        {
            writer_epoch = 1,
            holder_id = "fixture",
            owner_token = "fixture",
            committed = new { id = generation, manifest_horizon = manifest.EditCheckpointId, objects }
        }));
        await store.PutAsync(CloudMetadataAuthority.PointerKey, pointer, new PantsCloudObjectWriteCondition.Unconditional(), CancellationToken.None);
        await store.PutAsync(CloudMetadataAuthority.SentinelKey, "midge cloud metadata authority v3 initialized\n"u8.ToArray(),
            new PantsCloudObjectWriteCondition.Unconditional(), CancellationToken.None);
    }
}
