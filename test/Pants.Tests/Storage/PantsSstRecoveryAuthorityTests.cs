using System.Buffers.Binary;
using Cntryl.Pants.Compatibility;

namespace Cntryl.Pants.Storage;

public sealed class PantsSstRecoveryAuthorityTests
{
    [Fact]
    public async Task ShouldRejectUnsupportedAuthoritativeSstUnderSalvageWithoutChangingItsManifest()
    {
        using var directory = Format9TestMetadata.CopyFixture("populated-local");
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options)) { }
        var path = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        var bytes = await File.ReadAllBytesAsync(path);
        var footer = bytes.AsSpan(bytes.Length - DiskFormat.SstFooterSize);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[64..], 6);
        BinaryPrimitives.WriteUInt32LittleEndian(footer[80..], DiskFormat.Crc32C(footer[..80]));
        await File.WriteAllBytesAsync(path, bytes);
        var manifestPath = Path.Combine(directory.Path, "manifest.snapshot.json");
        var manifest = await File.ReadAllBytesAsync(manifestPath);

        await Assert.ThrowsAsync<PantsCompatibilityException>(async () =>
        {
            await using var reopened = await PantsDatabase.OpenAsync(options.WithRecoveryPolicy(PantsRecoveryPolicy.Salvage));
        });

        Assert.Equal(manifest, await File.ReadAllBytesAsync(manifestPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }
}
