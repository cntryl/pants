using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cntryl.Pants.Compatibility;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class ManifestKeyBoundEncodingTests
{
    /// <summary>FORMAT 9 manifest binary bounds survive checkpoint and recovery.</summary>
    [Fact]
    public async Task ShouldPersistBinaryKeyBoundsAndReadThemBack()
    {
        using var directory = new TemporaryDirectory();
        byte[] key = [0, 127, 128, 255, 107];
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await FlushDeleteAsync(options, key);

        foreach (var name in new[] { "manifest.snapshot.json" })
        {
            using var document = MetadataFrameCodec.Decode(File.ReadAllBytes(Path.Combine(directory.Path, name)), 1);
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
    [InlineData("smallest_key", 256)]
    [InlineData("smallest_key", -1)]
    [InlineData("largest_key", 256)]
    [InlineData("largest_key", -1)]
    public async Task ShouldRefuseManifestWhoseKeyBoundHoldsANonByteValue(string boundName, int value)
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await FlushDeleteAsync(options, [1, 2, 3]);
        foreach (var name in new[] { "manifest.snapshot.json" })
        {
            var path = Path.Combine(directory.Path, name);
            var manifest = Format9TestMetadata.ReadNode(path);
            manifest["files"]![0]![boundName]![1] = value;
            // Encode as intent to deliberately retain array bounds, then give the frame manifest authority.
            var invalid = MetadataFrameCodec.Encode(3, Encoding.UTF8.GetBytes(manifest.ToJsonString()));
            invalid[8] = 1;
            BinaryPrimitives.WriteUInt32BigEndian(invalid.AsSpan(invalid.Length - 4), DiskFormat.Crc32C(invalid.AsSpan(0, invalid.Length - 4)));
            File.WriteAllBytes(path, invalid);
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
