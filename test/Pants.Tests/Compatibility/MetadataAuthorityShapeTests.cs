using System.Text.Json;

namespace Cntryl.Pants.Compatibility;

public sealed class MetadataAuthorityShapeTests
{
    [Theory]
    [InlineData("files", "null")]
    [InlineData("files", "[null]")]
    [InlineData("column_families", "null")]
    [InlineData("column_families", "[null]")]
    [InlineData("retired_ssts", "null")]
    [InlineData("retired_ssts", "{\"000000_00_00000000000000000001.sst\":null}")]
    [InlineData("next_sst_seqs", "null")]
    public void ShouldRejectNullManifestAuthorityWithATypedCorruptionError(string field, string json)
    {
        using var value = JsonDocument.Parse(json);
        var manifest = new Dictionary<string, JsonElement>
        {
            ["flushed_through"] = JsonSerializer.SerializeToElement(new[] { new { cf_id = 0, through_sequence = 0 } }),
            [field] = value.RootElement
        };
        var bytes = MetadataFrameCodec.Encode(1, JsonSerializer.SerializeToUtf8Bytes(manifest));
        Assert.Throws<PantsCorruptionException>(() => ManifestCodec.Decode(bytes));
    }
}
