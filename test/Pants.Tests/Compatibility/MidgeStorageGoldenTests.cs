using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeStorageGoldenTests
{
    const string PinnedMidgeSha = "7d39f86217fdb07191a83bd885a514dd5ea9723f";

    static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Theory]
    [InlineData("wal/active/wal.log")]
    [InlineData("wal/sealed/00000000000000000001.wal")]
    public void ShouldRejectPinnedLegacyWalSegmentAsIncompatible(string relativePath)
    {
        Assert.Throws<PantsCompatibilityException>(() =>
            WalFrameReader.Visit(ReadStorageFixture(relativePath), static (_, _) => { }));
    }

    [Fact]
    public void ShouldRejectPinnedMidgeV4SstAsIncompatible()
    {
        var path = StorageFixturePath("sst/structured-v4.sst");
        var bytes = File.ReadAllBytes(path);

        Assert.Throws<PantsCompatibilityException>(() => SstCodec.Decode(bytes));
        Assert.Throws<PantsCompatibilityException>(() => SstReader.Open(path));
    }

    [Fact]
    public void ShouldRejectLegacyMidgeManifestJournal()
    {
        var journal = ReadStorageFixture("metadata/manifest.journal");

        Assert.Throws<PantsCorruptionException>(() => LocalDiskStore.ValidateManifestJournal(journal));
    }

    [Fact]
    public async Task ShouldRejectPinnedLegacyMidgeCloudLease()
    {
        var cloudBytes = ReadStorageFixture("leases/cloud.midge_primary_lease");
        var objects = new TestCloudObjectStore();
        objects.Seed(PantsCloudObjectLayout.LeaseObjectKey, cloudBytes);
        var store = new CloudObjectLeaseStore(objects, PantsCloudObjectLayout.LeaseObjectKey);

        await Assert.ThrowsAsync<PantsLeaseIndeterminateException>(async () => await store.ReadAsync(CancellationToken.None));
        Assert.Equal(cloudBytes, objects.Data.ToArray());
    }

    [Fact]
    public void ShouldRejectPinnedLegacyMidgeDdlRegistry()
    {
        Assert.Throws<PantsCorruptionException>(() => CloudDdlJson.DeserializeRegistry(ReadStorageFixture("ddl/ddl.registry.json")));
    }

    [Fact]
    public void ShouldRejectPinnedLegacyMidgeCloudWalCatalog()
    {
        Assert.Throws<PantsCompatibilityException>(() => JsonSerializer.Deserialize<ProviderWalCatalog>(
            ReadStorageFixture("cloud/publication-catalog.v1.json"), CatalogJsonOptions));
    }

    [Fact]
    public async Task ShouldRejectLegacyMidgeDatabaseOfflineWithoutMutation()
    {
        var path = StorageFixturePath("databases/midge-structured-v4-db");
        var before = ComputeDirectoryHash(path);

        await Assert.ThrowsAsync<PantsCompatibilityException>(() => PantsDatabase.VerifyPathAsync(path).AsTask());


        Assert.Equal(before, ComputeDirectoryHash(path));
    }

    static byte[] ReadStorageFixture(string relativePath) =>
        File.ReadAllBytes(StorageFixturePath(relativePath));

    static string ComputeDirectoryHash(string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entryPath in Directory
                     .EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
                     .OrderBy(
                         entry => Path.GetRelativePath(path, entry)
                             .Replace(Path.DirectorySeparatorChar, '/'),
                         StringComparer.Ordinal))
        {
            var isDirectory = Directory.Exists(entryPath);
            var relativePath = Path.GetRelativePath(path, entryPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            var typedRelativePath = $"{(isDirectory ? 'D' : 'F')}:{relativePath}";
            var relativePathBytes = Encoding.UTF8.GetBytes(typedRelativePath);
            AppendLength(hash, relativePathBytes.Length);
            hash.AppendData(relativePathBytes);
            var fileLength = isDirectory ? 0 : new FileInfo(entryPath).Length;
            AppendLength(hash, fileLength);
            if (!isDirectory)
            {
                hash.AppendData(File.ReadAllBytes(entryPath));
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static void AppendLength(IncrementalHash hash, long length)
    {
        var encoded = (Span<byte>)stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(encoded, length);
        hash.AppendData(encoded);
    }

    static string StorageFixturePath(string relativePath) =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Compatibility",
            "Storage",
            PinnedMidgeSha,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
}
