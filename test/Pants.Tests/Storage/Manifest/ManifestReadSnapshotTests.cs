using System.Text.Json;

namespace Cntryl.Pants.Storage.Manifest;

public sealed class ManifestReadSnapshotTests
{
    [Fact]
    public void ShouldOwnCompleteCheckpointStateGivenSourceManifestChanges()
    {
        var manifest = ManifestState.CreateInitial();
        ManifestReadSnapshot snapshot;
        using (var document = JsonDocument.Parse(
                   """{"checkpoint_sequence":17,"covering_ssts":["sst-0001.sst"]}"""))
        {
            manifest.CloudCheckpoint = document.RootElement;
            manifest.EditCheckpointId = 23;
            manifest.FlushedThrough[0] = 17;
            manifest.WalHistoryStart = 4;
            snapshot = ManifestReadSnapshot.Create(manifest);
        }

        manifest.CloudCheckpoint = null;
        manifest.EditCheckpointId = 24;
        manifest.FlushedThrough[0] = 18;
        manifest.WalHistoryStart = 5;

        var checkpoint = Assert.IsType<JsonElement>(snapshot.CloudCheckpoint);
        Assert.Equal(17UL, checkpoint.GetProperty("checkpoint_sequence").GetUInt64());
        Assert.Equal(
            "sst-0001.sst",
            Assert.Single(checkpoint.GetProperty("covering_ssts").EnumerateArray()).GetString());
        Assert.Equal(23UL, snapshot.EditCheckpointId);
        Assert.Equal(17UL, snapshot.FlushedThrough[0]);
        Assert.Equal(4UL, snapshot.WalHistoryStart);
    }
}
