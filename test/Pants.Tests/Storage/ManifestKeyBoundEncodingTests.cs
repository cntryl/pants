using System.Text.Json;
using System.Text.Json.Nodes;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class ManifestKeyBoundEncodingTests
{
    /// <summary>
    ///     The manifest is a Midge-pinned format whose key bounds are arrays of byte values, not
    ///     base64 strings, in the snapshot and in its legacy mirror.
    /// </summary>
    [Fact]
    public async Task ShouldPersistKeyBoundsAsArraysOfByteValuesAndReadThemBack()
    {
        using var directory = new TemporaryDirectory();
        byte[] key = [0, 127, 128, 255, 107];
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await FlushDeleteAsync(options, key);

        foreach (var name in new[] { "manifest.snapshot.json", "manifest.json" })
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory.Path, name)));
            var file = Assert.Single(document.RootElement.GetProperty("files").EnumerateArray());
            foreach (var bound in new[] { "smallest_key", "largest_key" })
            {
                var values = file.GetProperty(bound).EnumerateArray().Select(static item => item.GetByte());
                Assert.Equal(key, values);
            }
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        var layout = await reopened.Diagnostics.GetStorageLayoutAsync();
        var stored = Assert.Single(layout.Levels.SelectMany(static level => level.Files));
        Assert.Equal(key, stored.SmallestKey!.Value.ToArray());
        Assert.Equal(key, stored.LargestKey!.Value.ToArray());
    }

    /// <summary>
    ///     A bound holding a value outside a byte cannot name a key, so the manifest that carries it
    ///     is not one the engine can trust.
    /// </summary>
    [Theory]
    [InlineData(256)]
    [InlineData(-1)]
    public async Task ShouldRefuseManifestWhoseKeyBoundHoldsANonByteValue(int value)
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await FlushDeleteAsync(options, [1, 2, 3]);
        foreach (var name in new[] { "manifest.snapshot.json", "manifest.json" })
        {
            var path = Path.Combine(directory.Path, name);
            var manifest = JsonNode.Parse(File.ReadAllBytes(path))!;
            manifest["files"]![0]!["smallest_key"]![1] = value;
            File.WriteAllText(path, manifest.ToJsonString());
        }

        var exception = await Assert.ThrowsAsync<PantsCorruptionException>(() =>
            PantsDatabase.OpenAsync(options).AsTask());
        Assert.Equal(PantsErrorCode.Corruption, exception.Code);
    }

    static async Task FlushDeleteAsync(PantsOpenOptions options, byte[] key)
    {
        await using var database = await PantsDatabase.OpenAsync(options);
        var family = database.ColumnFamilies.DefaultFamily;
        await using (var transaction = await database.Transactions.BeginAsync(
                         family,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Delete(key);
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        await database.Maintenance.FlushAsync(family);
    }
}
