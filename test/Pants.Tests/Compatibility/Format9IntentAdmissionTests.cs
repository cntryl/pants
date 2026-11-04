using System.Text.Json;
using System.Text.Json.Nodes;
using Cntryl.Pants.Storage.Internal;
using Cntryl.Pants.Storage.Internal.Recovery;

namespace Cntryl.Pants.Compatibility;

public sealed class Format9IntentAdmissionTests
{
    [Theory]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"name\":\"source.sst\",\"level\":0}]")]
    public void ShouldRejectMalformedCapturedIdentityGivenCompleteMoveIntent(string captured)
    {
        var entry = new JsonObject
        {
            ["MoveSstsPublish"] = new JsonObject
            {
                ["phase"] = "OutputDurable",
                ["target_level"] = 1,
                ["captured"] = JsonNode.Parse(captured)
            }
        };

        Assert.Throws<PantsCorruptionException>(() => IntentLogCodec.Read(Frame(entry)));
    }

    [Theory]
    [InlineData("key_bounds_complete", "null")]
    [InlineData("key_bounds_complete", "1")]
    [InlineData("content_crc32c", "4294967296")]
    [InlineData("earliest_expiration_millis", "-1")]
    public void ShouldRejectMalformedRuntimeMetadataGivenCompleteSstIntent(string field, string value)
    {
        var file = JsonNode.Parse("""
            {"name":"output.sst","level":0,"size_bytes":1,"cf_id":0,
             "smallest_key":null,"largest_key":null,"smallest_seq":null,"largest_seq":null}
            """)!;
        file[field] = JsonNode.Parse(value);
        var entry = new JsonObject { ["SstAdded"] = new JsonObject { ["file_meta"] = file } };

        Assert.Throws<PantsCorruptionException>(() => IntentLogCodec.Read(Frame(entry)));
    }

    static byte[] Frame(JsonNode entry) => MetadataFrameCodec.Encode(3,
        JsonSerializer.SerializeToElement(new
        {
            sequence = 1UL,
            previous_checksum = 0U,
            keep = 0,
            append = new[] { entry }
        }));
}
