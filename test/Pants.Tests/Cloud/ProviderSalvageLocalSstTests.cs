using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderSalvageLocalSstTests
{
    static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    static readonly byte[] SstBytes = [.. Enumerable.Range(0, 200).Select(static value => (byte)value)];

    [Fact]
    public async Task ShouldServeVerifiedLocalSstUnderSalvageWhenRemoteIsMissing()
    {
        using var cache = new TemporaryDirectory();
        WriteLocalSst(cache.Path, SstBytes);
        var control = await SeedManifestAsync();

        var result = await HydrateAsync(
            cache.Path,
            control,
            new CountingCloudObjectStore(),
            PantsRecoveryPolicy.Salvage);

        Assert.True(result.RequiresSalvage);
        Assert.Equal(SstBytes, File.ReadAllBytes(Path.Combine(cache.Path, "sst", "a.sst")));
    }

    [Fact]
    public async Task ShouldServeVerifiedLocalSstUnderSalvageWhenRemoteIsTruncated()
    {
        using var cache = new TemporaryDirectory();
        WriteLocalSst(cache.Path, SstBytes);
        var control = await SeedManifestAsync();
        var sst = new CountingCloudObjectStore();
        await PutSstAsync(sst, SstBytes[..^1]);

        var result = await HydrateAsync(cache.Path, control, sst, PantsRecoveryPolicy.Salvage);

        Assert.True(result.RequiresSalvage);
    }

    [Fact]
    public async Task ShouldKeepFailingStrictWhenRemoteIsMissingEvenWithAVerifiedLocalCopy()
    {
        using var cache = new TemporaryDirectory();
        WriteLocalSst(cache.Path, SstBytes);
        var control = await SeedManifestAsync();

        await Assert.ThrowsAsync<PantsRecoveryFailedException>(async () =>
            await HydrateAsync(
                cache.Path,
                control,
                new CountingCloudObjectStore(),
                PantsRecoveryPolicy.Strict));
    }

    [Fact]
    public async Task ShouldRefuseALocalCopyThatFailsVerificationEvenUnderSalvage()
    {
        using var cache = new TemporaryDirectory();
        var corrupt = SstBytes.ToArray();
        corrupt[10] ^= 0xFF;
        WriteLocalSst(cache.Path, corrupt);
        var control = await SeedManifestAsync();

        await Assert.ThrowsAsync<PantsRecoveryFailedException>(async () =>
            await HydrateAsync(
                cache.Path,
                control,
                new CountingCloudObjectStore(),
                PantsRecoveryPolicy.Salvage));
    }

    [Fact]
    public async Task ShouldFailSalvageWhenThereIsNoLocalCopyAtAll()
    {
        using var cache = new TemporaryDirectory();
        var control = await SeedManifestAsync();

        await Assert.ThrowsAsync<PantsRecoveryFailedException>(async () =>
            await HydrateAsync(
                cache.Path,
                control,
                new CountingCloudObjectStore(),
                PantsRecoveryPolicy.Salvage));
    }

    static void WriteLocalSst(string root, byte[] bytes)
    {
        Directory.CreateDirectory(Path.Combine(root, "sst"));
        File.WriteAllBytes(Path.Combine(root, "sst", "a.sst"), bytes);
    }

    static async Task PutSstAsync(CountingCloudObjectStore store, byte[] bytes) =>
        Assert.True(await store.PutAsync(
            PantsCloudObjectLayout.SstPrefix + "a.sst",
            bytes,
            new PantsCloudObjectWriteCondition.Unconditional(),
            CancellationToken.None));

    static async Task<CountingCloudObjectStore> SeedManifestAsync()
    {
        var manifest = new ManifestState
        {
            LastPersistedSequence = 3,
            Files =
            [
                new FileMeta
                {
                    Name = "a.sst",
                    SizeBytes = (ulong)SstBytes.Length,
                    ContentCrc32C = DiskFormat.Crc32C(SstBytes)
                }
            ]
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ManifestOptions);
        var control = new CountingCloudObjectStore();
        Assert.True(await control.PutAsync(
            PantsCloudObjectLayout.MetadataPrefix + "manifest.json",
            bytes,
            new PantsCloudObjectWriteCondition.Unconditional(),
            CancellationToken.None));
        return control;
    }

    static ValueTask<ProviderCloudHydrationResult> HydrateAsync(
        string cachePath,
        ICloudObjectStore control,
        ICloudObjectStore sst,
        PantsRecoveryPolicy policy) =>
        ProviderCloudPersistence.HydrateLocalCacheAsync(
            cachePath,
            new CountingCloudObjectStore(),
            sst,
            control,
            policy,
            CancellationToken.None);
}
