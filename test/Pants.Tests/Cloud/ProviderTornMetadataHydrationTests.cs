using System.Text;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderTornMetadataHydrationTests
{
    [Fact]
    public async Task ShouldRejectMutableLegacyMetadataDuringStrictHydration()
    {
        using var cache = new TemporaryDirectory();
        var control = await SeedAsync(3, 5);
        var original = await control.GetAsync("metadata/manifest.snapshot.json", CancellationToken.None);
        await Assert.ThrowsAsync<PantsCompatibilityException>(async () => await HydrateAsync(cache.Path, control, PantsRecoveryPolicy.Strict));
        Assert.False(File.Exists(Path.Combine(cache.Path, "manifest.snapshot.json")));
        Assert.Equal(original!.Data.ToArray(), (await control.GetAsync("metadata/manifest.snapshot.json", CancellationToken.None))!.Data.ToArray());
    }

    [Fact]
    public async Task ShouldRejectMutableLegacyMetadataDuringSalvageHydration()
    {
        using var cache = new TemporaryDirectory();
        var control = await SeedAsync(3, 5);
        await Assert.ThrowsAsync<PantsCompatibilityException>(async () => await HydrateAsync(cache.Path, control, PantsRecoveryPolicy.Salvage));
        Assert.False(File.Exists(Path.Combine(cache.Path, "manifest.snapshot.json")));
        Assert.False(File.Exists(Path.Combine(cache.Path, "manifest.json")));
    }

    [Fact]
    public async Task ShouldHydratePointerSelectedSnapshotGivenConflictingMutableLegacyCopy()
    {
        using var cache = new TemporaryDirectory();
        var control = new CountingCloudObjectStore();
        var manifest = ManifestState.CreateInitial();
        manifest.EditCheckpointId = 4;
        await CloudTestMetadata.SeedAsync(control, manifest);
        await PutAsync(control, "manifest.json", ManifestJson(99));
        var result = await HydrateAsync(cache.Path, control, PantsRecoveryPolicy.Strict);
        Assert.False(result.RequiresSalvage);
        Assert.Equal(4UL, ManifestCodec.Decode(await File.ReadAllBytesAsync(Path.Combine(cache.Path, "manifest.snapshot.json"))).EditCheckpointId);
        Assert.False(File.Exists(Path.Combine(cache.Path, "manifest.json")));
    }

    [Fact]
    public async Task ShouldPropagateSelectedObjectReadFailureUnderBothRecoveryPolicies()
    {
        using var strictCache = new TemporaryDirectory();
        using var salvageCache = new TemporaryDirectory();
        var control = new CountingCloudObjectStore();
        await CloudTestMetadata.SeedAsync(control, ManifestState.CreateInitial());
        var pointer = await control.GetAsync(CloudMetadataAuthority.PointerKey, CancellationToken.None);
        using var descriptor = MetadataFrameCodec.Decode(pointer!.Data.Span, 4);
        var key = descriptor.RootElement.GetProperty("committed").GetProperty("objects").EnumerateArray()
            .Single(item => item.GetProperty("file_name").GetString() == "manifest.snapshot.json").GetProperty("object_key").GetString()!;
        var failing = new FailingReadStore(control, key);
        await Assert.ThrowsAsync<PantsIOException>(async () => await HydrateAsync(strictCache.Path, failing, PantsRecoveryPolicy.Strict));
        await Assert.ThrowsAsync<PantsIOException>(async () => await HydrateAsync(salvageCache.Path, failing, PantsRecoveryPolicy.Salvage));
        Assert.False(File.Exists(Path.Combine(strictCache.Path, "manifest.snapshot.json")));
        Assert.False(File.Exists(Path.Combine(salvageCache.Path, "manifest.snapshot.json")));
    }

    static async Task<CountingCloudObjectStore> SeedAsync(ulong snapshotSequence, ulong manifestSequence)
    {
        var control = new CountingCloudObjectStore();
        await PutAsync(control, "manifest.snapshot.json", ManifestJson(snapshotSequence));
        await PutAsync(control, "manifest.json", ManifestJson(manifestSequence));
        await PutAsync(control, "intent_log.json", "[]");
        return control;
    }

    static string ManifestJson(ulong sequence) =>
        $$"""{"last_persisted_sequence":{{sequence}},"files":[],"column_families":[]}""";

    static async Task PutAsync(CountingCloudObjectStore store, string name, string text) =>
        Assert.True(await store.PutAsync(
            PantsCloudObjectLayout.MetadataPrefix + name,
            Encoding.UTF8.GetBytes(text),
            new PantsCloudObjectWriteCondition.Unconditional(),
            CancellationToken.None));

    static ValueTask<ProviderCloudHydrationResult> HydrateAsync(
        string cachePath,
        ICloudObjectStore control,
        PantsRecoveryPolicy policy) =>
        ProviderCloudPersistence.HydrateLocalCacheAsync(
            cachePath,
            new CountingCloudObjectStore(),
            new CountingCloudObjectStore(),
            control,
            policy,
            static _ => ValueTask.CompletedTask,
            CancellationToken.None);

    sealed class FailingReadStore(ICloudObjectStore inner, string failingKey) : ICloudObjectStore
    {
        public ValueTask<CloudObject?> GetAsync(string objectKey, CancellationToken cancellationToken) =>
            objectKey == failingKey
                ? throw new PantsIOException("Injected read failure.")
                : inner.GetAsync(objectKey, cancellationToken);

        public ValueTask<CloudObject?> GetRangeAsync(
            string objectKey,
            ulong offset,
            int length,
            CancellationToken cancellationToken) =>
            objectKey == failingKey
                ? throw new PantsIOException("Injected read failure.")
                : inner.GetRangeAsync(objectKey, offset, length, cancellationToken);

        public ValueTask<CloudObjectMetadata?> HeadAsync(string objectKey, CancellationToken cancellationToken) =>
            inner.HeadAsync(objectKey, cancellationToken);

        public ValueTask<bool> PutAsync(
            string objectKey,
            ReadOnlyMemory<byte> data,
            CloudObjectWriteCondition condition,
            CancellationToken cancellationToken) =>
            inner.PutAsync(objectKey, data, condition, cancellationToken);

        public ValueTask<CloudObjectListPage> ListPageAsync(
            string prefix,
            string? continuationToken,
            CancellationToken cancellationToken) =>
            inner.ListPageAsync(prefix, continuationToken, cancellationToken);

        public ValueTask<CloudObjectDeleteOutcome> DeleteAsync(
            string objectKey,
            CloudObjectDeleteCondition condition,
            CancellationToken cancellationToken) =>
            inner.DeleteAsync(objectKey, condition, cancellationToken);
    }
}
