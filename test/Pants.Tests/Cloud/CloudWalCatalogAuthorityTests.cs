using System.Text.Json;
using System.Text.Json.Nodes;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class CloudWalCatalogAuthorityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldUseValidPrimaryWithoutDecodingUnsupportedMirror(bool repairCopies)
    {
        var store = new ScriptedCloudObjectStore();
        var primary = EncodeCatalog();
        var mirror = "{\"format_version\":4}"u8.ToArray();
        store.Write(PantsCloudObjectLayout.WalCatalogObjectKey, primary);
        store.Write(PantsCloudObjectLayout.WalCatalogMirrorObjectKey, mirror);
        var catalogs = new ProviderWalCatalogStore(
            store,
            static bytes => ProviderCloudPersistence.DecodeCatalogForFloor(bytes.Span),
            static () => { },
            static _ => ValueTask.CompletedTask);

        using var read = await catalogs.ReadAsync(false, CancellationToken.None, repairCopies: repairCopies);

        Assert.Equal(7UL, read.Catalog!.FencingEpoch);
        Assert.Equal(primary, store.Read(PantsCloudObjectLayout.WalCatalogObjectKey));
        Assert.Equal(repairCopies ? primary : mirror, store.Read(PantsCloudObjectLayout.WalCatalogMirrorObjectKey));
    }

    [Fact]
    public void ShouldSelectValidSimulatedPrimaryWithoutRepairingEitherCatalogCopyDuringAnUnleasedProbe()
    {
        using var directory = new TemporaryDirectory();
        var walDirectory = Path.Combine(directory.Path, "cloud_store", "wal");
        Directory.CreateDirectory(walDirectory);
        var primary = EncodeCatalog();
        var primaryPath = Path.Combine(walDirectory, "publication-catalog.v1.json");
        var mirrorPath = Path.Combine(walDirectory, "publication-catalog.v1.mirror.json");
        var mirror = "{\"format_version\":4}"u8.ToArray();
        File.WriteAllBytes(primaryPath, primary);
        File.WriteAllBytes(mirrorPath, mirror);

        var selected = SimulatedCloudPersistence.PrepareLocalCache(directory.Path, PantsRecoveryPolicy.Strict);

        Assert.Equal(7UL, selected.MinimumWriterEpoch);
        Assert.Equal(10UL, selected.NextWalSegmentId);
        Assert.False(selected.RequiresSalvage);
        Assert.Equal(primary, File.ReadAllBytes(primaryPath));
        Assert.Equal(mirror, File.ReadAllBytes(mirrorPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldNotReplaceUnsupportedPrimaryFromValidMirror(bool simulated)
    {
        using var directory = new TemporaryDirectory();
        var primary = "{\"format_version\":4}"u8.ToArray();
        var mirror = EncodeCatalog();
        if (simulated)
        {
            var walDirectory = Path.Combine(directory.Path, "cloud_store", "wal");
            Directory.CreateDirectory(walDirectory);
            var primaryPath = Path.Combine(walDirectory, "publication-catalog.v1.json");
            var mirrorPath = Path.Combine(walDirectory, "publication-catalog.v1.mirror.json");
            File.WriteAllBytes(primaryPath, primary);
            File.WriteAllBytes(mirrorPath, mirror);

            Assert.Throws<PantsCompatibilityException>(() =>
                SimulatedCloudPersistence.PrepareLocalCache(directory.Path, PantsRecoveryPolicy.Strict));

            Assert.Equal(primary, File.ReadAllBytes(primaryPath));
            Assert.Equal(mirror, File.ReadAllBytes(mirrorPath));
            return;
        }

        var store = new ScriptedCloudObjectStore();
        store.Write(PantsCloudObjectLayout.WalCatalogObjectKey, primary);
        store.Write(PantsCloudObjectLayout.WalCatalogMirrorObjectKey, mirror);
        var catalogs = new ProviderWalCatalogStore(
            store,
            static bytes => ProviderCloudPersistence.DecodeCatalogForFloor(bytes.Span),
            static () => { },
            static _ => ValueTask.CompletedTask);

        await Assert.ThrowsAsync<PantsCompatibilityException>(async () =>
        {
            using var read = await catalogs.ReadAsync(false, CancellationToken.None);
        });

        Assert.Equal(primary, store.Read(PantsCloudObjectLayout.WalCatalogObjectKey));
        Assert.Equal(mirror, store.Read(PantsCloudObjectLayout.WalCatalogMirrorObjectKey));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(7UL)]
    public void ShouldAcceptLinkedCatalogHistoryWithSkippedAllocatedSegmentIds(ulong firstWriterEpoch)
    {
        var first = TestWalSegment.Create(1, firstWriterEpoch, 1, "first.wal", [1]);
        var successor = TestWalSegment.Create(4, 7, 2, "successor.wal", [2], first.Facts.Link);
        var bytes = EncodeCatalog(new SortedDictionary<ulong, ProviderPublishedWalSegment>
        {
            [1] = TestWalSegment.Publish(first),
            [4] = TestWalSegment.Publish(successor)
        });

        var catalog = ProviderCloudPersistence.DecodeCatalogForFloor(bytes);

        Assert.Equal([1UL, 4UL], catalog.Segments.Keys);
        Assert.Equal(10UL, catalog.NextSegmentId);
        Assert.True(catalog.Segments[4].Previous!.Matches(first.Facts.Link));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"format_version\":3}")]
    [InlineData("{\"format_version\":\"three\"}")]
    public async Task ShouldRepairStructurallyInvalidPrimaryFromValidMirror(string invalidPrimary)
    {
        var store = new ScriptedCloudObjectStore();
        var mirror = EncodeCatalog();
        store.Write(PantsCloudObjectLayout.WalCatalogObjectKey, System.Text.Encoding.UTF8.GetBytes(invalidPrimary));
        store.Write(PantsCloudObjectLayout.WalCatalogMirrorObjectKey, mirror);
        var catalogs = new ProviderWalCatalogStore(
            store,
            static bytes => ProviderCloudPersistence.DecodeCatalogForFloor(bytes.Span),
            static () => { },
            static _ => ValueTask.CompletedTask);

        using var read = await catalogs.ReadAsync(false, CancellationToken.None);

        Assert.Equal(7UL, read.Catalog!.FencingEpoch);
        Assert.Equal(mirror, store.Read(PantsCloudObjectLayout.WalCatalogObjectKey));
        Assert.Equal(mirror, store.Read(PantsCloudObjectLayout.WalCatalogMirrorObjectKey));
    }

    [Theory]
    [InlineData("anchor", "null")]
    [InlineData("anchor.digest", "null")]
    [InlineData("segments", "null")]
    [InlineData("unknown", "0")]
    public void ShouldClassifyInvalidCatalogStructureAsCorruption(string path, string json)
    {
        var root = JsonNode.Parse(EncodeCatalog())!.AsObject();
        if (path == "anchor.digest")
        {
            root["anchor"]!["digest"] = JsonNode.Parse(json);
        }
        else
        {
            root[path] = JsonNode.Parse(json);
        }

        Assert.Throws<PantsCorruptionException>(() =>
            ProviderCloudPersistence.DecodeCatalogForFloor(JsonSerializer.SerializeToUtf8Bytes(root)));
    }

    [Fact]
    public void ShouldRejectDuplicateCatalogFields()
    {
        var json = System.Text.Encoding.UTF8.GetString(EncodeCatalog());
        var duplicate = System.Text.Encoding.UTF8.GetBytes(json.Replace(
            "\"fencing_epoch\":7", "\"fencing_epoch\":7,\"fencing_epoch\":7", StringComparison.Ordinal));

        Assert.Throws<PantsCorruptionException>(() =>
            ProviderCloudPersistence.DecodeCatalogForFloor(duplicate));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(7UL)]
    public void ShouldHydrateLinkedCatalogWithSkippedSegmentIdsAndRetainReservationFloor(ulong firstWriterEpoch)
    {
        using var directory = new TemporaryDirectory();
        var cloudRoot = Path.Combine(directory.Path, "cloud_store");
        var first = TestWalSegment.Create(1, firstWriterEpoch, 1, "first.wal", [1]);
        var successor = TestWalSegment.Create(4, 7, 2, "successor.wal", [2], first.Facts.Link);
        var segments = new SortedDictionary<ulong, ProviderPublishedWalSegment>
        {
            [1] = TestWalSegment.Publish(first),
            [4] = TestWalSegment.Publish(successor)
        };
        foreach (var segment in new[] { first, successor })
        {
            var path = Path.Combine(cloudRoot, segments[segment.SegmentId].ObjectKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, segment.Bytes);
        }

        File.WriteAllBytes(Path.Combine(cloudRoot, "wal", "publication-catalog.v1.json"), EncodeCatalog(segments));

        var hydrated = SimulatedCloudPersistence.PrepareLocalCache(directory.Path, PantsRecoveryPolicy.Strict);

        Assert.Equal(10UL, hydrated.NextWalSegmentId);
        Assert.True(hydrated.WalHead!.Matches(successor.Facts.Link));
        Assert.Equal(first.Bytes, File.ReadAllBytes(Path.Combine(directory.Path, "wal", "00000000000000000001.wal")));
        Assert.Equal(successor.Bytes, File.ReadAllBytes(Path.Combine(directory.Path, "wal", "00000000000000000004.wal")));
    }

    static byte[] EncodeCatalog(SortedDictionary<ulong, ProviderPublishedWalSegment>? segments = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new ProviderWalCatalog
        {
            FencingEpoch = 7,
            NextSegmentId = 10,
            Segments = segments ?? []
        });
}
