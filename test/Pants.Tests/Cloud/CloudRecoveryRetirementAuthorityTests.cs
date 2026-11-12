using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudRecoveryRetirementAuthorityTests
{
    [Fact]
    public async Task ShouldRetryChangedRetirementAuthorityBeforeStagingSelectedMetadata()
    {
        using var directory = new TemporaryDirectory();
        var store = new ScriptedCloudObjectStore();
        await SeedAsync(store, false);
        var newerCatalog = store.Read(PantsCloudObjectLayout.WalCatalogObjectKey);
        var catalog = JsonSerializer.Deserialize<ProviderWalCatalog>(newerCatalog)!;
        store.Write(PantsCloudObjectLayout.WalCatalogObjectKey,
            JsonSerializer.SerializeToUtf8Bytes(catalog with { RetiredBy = 1 }));
        var advanced = 0;
        store.BeforeRangeRead = (key, _) =>
        {
            if (key == CloudMetadataAuthority.PointerKey && Interlocked.Exchange(ref advanced, 1) == 0)
            {
                store.Write(PantsCloudObjectLayout.WalCatalogObjectKey, newerCatalog);
            }

            return ValueTask.CompletedTask;
        };

        await Assert.ThrowsAsync<PantsBusyException>(() =>
            ProviderCloudPersistence.HydrateLocalCacheAsync(directory.Path, store, store, store,
                PantsRecoveryPolicy.Strict, static _ => ValueTask.CompletedTask, CancellationToken.None).AsTask());

        Assert.False(File.Exists(Path.Combine(directory.Path, "FORMAT")));
        Assert.False(File.Exists(Path.Combine(directory.Path, "manifest.snapshot.json")));
    }

    [Theory]
    [InlineData(false, false, PantsRecoveryPolicy.Strict)]
    [InlineData(false, true, PantsRecoveryPolicy.Strict)]
    [InlineData(true, false, PantsRecoveryPolicy.Strict)]
    [InlineData(true, true, PantsRecoveryPolicy.Strict)]
    [InlineData(false, false, PantsRecoveryPolicy.Salvage)]
    [InlineData(false, true, PantsRecoveryPolicy.Salvage)]
    [InlineData(true, false, PantsRecoveryPolicy.Salvage)]
    [InlineData(true, true, PantsRecoveryPolicy.Salvage)]
    public async Task ShouldRejectMetadataOlderThanWalRetirementBeforeChangingCache(
        bool simulated, bool uncommitted, PantsRecoveryPolicy recoveryPolicy)
    {
        using var directory = new TemporaryDirectory();
        ICloudObjectStore store = simulated
            ? new SimulatedCloudObjectStore(Path.Combine(directory.Path, "cloud_store"))
            : new CountingCloudObjectStore();
        await SeedAsync(store, uncommitted);
        var localSnapshot = Path.Combine(directory.Path, "manifest.snapshot.json");
        byte[]? original = null;
        if (!uncommitted)
        {
            var local = ManifestState.CreateInitial();
            local.EditCheckpointId = 7;
            original = ManifestCodec.Encode(local,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
            File.WriteAllBytes(localSnapshot, original);
        }

        if (simulated)
        {
            Assert.Throws<PantsRecoveryFailedException>(() =>
                SimulatedCloudPersistence.PrepareLocalCache(directory.Path, recoveryPolicy));
        }
        else
        {
            await Assert.ThrowsAsync<PantsRecoveryFailedException>(() =>
                ProviderCloudPersistence.HydrateLocalCacheAsync(directory.Path, store, store, store,
                    recoveryPolicy, static _ => ValueTask.CompletedTask, CancellationToken.None).AsTask());
        }

        if (original is not null)
        {
            Assert.Equal(original, File.ReadAllBytes(localSnapshot));
        }
        else
        {
            Assert.False(File.Exists(localSnapshot));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRecoverMetadataAtWalRetirementGeneration(bool simulated)
    {
        using var directory = new TemporaryDirectory();
        ICloudObjectStore store = simulated
            ? new SimulatedCloudObjectStore(Path.Combine(directory.Path, "cloud_store"))
            : new CountingCloudObjectStore();
        await SeedAsync(store, false);
        var current = ManifestState.CreateInitial();
        current.EditCheckpointId = 9;
        current.LastPersistedSequence = 9;
        current.FlushedThrough[0] = 9;
        current.WalHistoryStart = 10;
        await CloudTestMetadata.SeedAsync(store, current);

        if (simulated)
        {
            var hydration = SimulatedCloudPersistence.PrepareLocalCache(directory.Path, PantsRecoveryPolicy.Strict);
            Assert.Equal(10UL, hydration.NextWalSegmentId);
            Assert.Equal(9UL, hydration.WalHead!.LastSeq);
        }
        else
        {
            var hydration = await ProviderCloudPersistence.HydrateLocalCacheAsync(directory.Path, store, store, store,
                PantsRecoveryPolicy.Strict, static _ => ValueTask.CompletedTask, CancellationToken.None);
            Assert.Equal(10UL, hydration.NextWalSegmentId);
            Assert.Equal(9UL, hydration.WalHead!.LastSeq);
        }

        Assert.Equal(9UL, CloudManifestReader.ReadManifest(directory.Path)!.LastPersistedSequence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectNonemptyCacheGivenNilCommittedPointerBeforeRemovingControlFiles(bool simulated)
    {
        using var directory = new TemporaryDirectory();
        ICloudObjectStore store = simulated
            ? new SimulatedCloudObjectStore(Path.Combine(directory.Path, "cloud_store"))
            : new CountingCloudObjectStore();
        await SeedAsync(store, true);
        await store.PutAsync(PantsCloudObjectLayout.WalCatalogObjectKey,
            JsonSerializer.SerializeToUtf8Bytes(new ProviderWalCatalog { FencingEpoch = 1, NextSegmentId = 1 }),
            new PantsCloudObjectWriteCondition.Unconditional(), CancellationToken.None);
        var local = ManifestState.CreateInitial();
        local.EditCheckpointId = 7;
        var bytes = ManifestCodec.Encode(local,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        var path = Path.Combine(directory.Path, "manifest.snapshot.json");
        File.WriteAllBytes(path, bytes);

        if (simulated)
        {
            Assert.Throws<PantsCorruptionException>(() =>
                SimulatedCloudPersistence.PrepareLocalCache(directory.Path, PantsRecoveryPolicy.Strict));
        }
        else
        {
            await Assert.ThrowsAsync<PantsCorruptionException>(() =>
                ProviderCloudPersistence.HydrateLocalCacheAsync(directory.Path, store, store, store,
                    PantsRecoveryPolicy.Strict, static _ => ValueTask.CompletedTask, CancellationToken.None).AsTask());
        }

        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    static async Task SeedAsync(ICloudObjectStore store, bool uncommitted)
    {
        if (uncommitted)
        {
            var pointer = MetadataFrameCodec.Encode(4, JsonSerializer.SerializeToElement(new
            {
                writer_epoch = 1,
                holder_id = "fixture",
                owner_token = "fixture",
                committed = (object?)null
            }));
            await store.PutAsync(CloudMetadataAuthority.PointerKey, pointer,
                new PantsCloudObjectWriteCondition.IfAbsent(), CancellationToken.None);
            await store.PutAsync(CloudMetadataAuthority.SentinelKey,
                "midge cloud metadata authority v3 initialized\n"u8.ToArray(),
                new PantsCloudObjectWriteCondition.IfAbsent(), CancellationToken.None);
        }
        else
        {
            await CloudTestMetadata.SeedAsync(store, ManifestState.CreateInitial());
        }

        var leaseStore = new CloudObjectLeaseStore(store, PantsCloudObjectLayout.LeaseObjectKey);
        await leaseStore.TryCreateAsync(new CloudLeaseRecord("fixture", 1, "fixture", DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(20)), CancellationToken.None);
        var retired = TestWalSegment.Create(9, 1, 9, "retired.wal", [9]);
        var catalog = new ProviderWalCatalog
        {
            FencingEpoch = 1,
            Anchor = retired.Facts.Link,
            SequenceFloor = 9,
            RetiredBy = 2,
            NextSegmentId = 10
        };
        await store.PutAsync(PantsCloudObjectLayout.WalCatalogObjectKey, JsonSerializer.SerializeToUtf8Bytes(catalog),
            new PantsCloudObjectWriteCondition.IfAbsent(), CancellationToken.None);
    }
}
