using System.Text.Json;

namespace Pants.Tests;

public sealed class MidgeContractManifestTests
{
    private const string PinnedSha = "c5ffc2d3284c76b6f7cd03444a5b0a38ae8bbc33";

    [Fact]
    public void ShouldConsumeCommittedManifestWithoutSiblingCheckout()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "MidgeContractManifest.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;
        JsonElement entries = root.GetProperty("entries");

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(PinnedSha, root.GetProperty("midgeSha").GetString());
        Assert.True(entries.GetArrayLength() > 900);
        Assert.All(entries.EnumerateArray(), static entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("source").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("sourceSymbolOrTest").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("observableBehavior").GetString()));
            Assert.True(entry.TryGetProperty("status", out _));
        });
    }
}
