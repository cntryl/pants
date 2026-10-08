using System.Text;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class ProviderTornMetadataHydrationTests
{
    [Fact]
    public async Task ShouldFailStrictHydrationGivenMismatchedManifestSequencesAndNoJournal()
    {
        using var cache = new TemporaryDirectory();
        var control = await SeedAsync(3, 5);

        await Assert.ThrowsAsync<PantsRecoveryFailedException>(async () =>
            await HydrateAsync(cache.Path, control, PantsRecoveryPolicy.Strict));

        Assert.False(File.Exists(Path.Combine(cache.Path, "manifest.json")));
    }

    [Fact]
    public async Task ShouldIgnoreOlderManifestAndReportSalvageGivenMismatchedSequences()
    {
        using var cache = new TemporaryDirectory();
        var control = await SeedAsync(3, 5);

        var result = await HydrateAsync(cache.Path, control, PantsRecoveryPolicy.Salvage);

        Assert.True(result.RequiresSalvage);
        Assert.False(File.Exists(Path.Combine(cache.Path, "manifest.snapshot.json")));
        Assert.True(File.Exists(Path.Combine(cache.Path, "manifest.json")));
    }

    [Fact]
    public async Task ShouldAcceptMatchingManifestSequences()
    {
        using var cache = new TemporaryDirectory();
        var control = await SeedAsync(4, 4);

        var result = await HydrateAsync(cache.Path, control, PantsRecoveryPolicy.Strict);

        Assert.False(result.RequiresSalvage);
    }

    [Fact]
    public async Task ShouldToleratePerObjectReadFailureOnlyUnderSalvage()
    {
        using var strictCache = new TemporaryDirectory();
        using var salvageCache = new TemporaryDirectory();
        var failing = new FailingReadStore(
            await SeedAsync(4, 4),
            PantsCloudObjectLayout.MetadataPrefix + "intent_log.json");

        await Assert.ThrowsAsync<PantsIOException>(async () =>
            await HydrateAsync(strictCache.Path, failing, PantsRecoveryPolicy.Strict));
        var result = await HydrateAsync(salvageCache.Path, failing, PantsRecoveryPolicy.Salvage);

        Assert.True(result.RequiresSalvage);
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
            inner.GetRangeAsync(objectKey, offset, length, cancellationToken);

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
