using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeStorageGoldenTests
{
    const string PinnedMidgeSha = "75dcc39f7a9b87df480ed91c3a5c93fe1389ca71";

    static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Theory]
    [InlineData("wal/active/wal.log")]
    [InlineData("wal/sealed/00000000000000000001.wal")]
    public void ShouldDecodeEveryFrameGivenPinnedMidgeWalSegmentGolden(string relativePath)
    {
        var records = new List<WalRecord>();

        WalFrameReader.Visit(
            ReadStorageFixture(relativePath),
            (record, _) => records.Add(record));

        Assert.NotEmpty(records);
        Assert.All(records, static record =>
        {
            Assert.True(record.Sequence > 0);
            Assert.True(record.WriterEpoch > 0);
        });
    }

    [Fact]
    public void ShouldDecodeEveryV4StructureGivenPinnedMidgeStructuredSstGolden()
    {
        var path = StorageFixturePath("sst/structured-v4.sst");
        var bytes = File.ReadAllBytes(path);

        var contents = SstCodec.Decode(bytes);
        using var reader = SstReader.Open(path);

        Assert.True(contents.Entries.Count >= 192);
        Assert.Single(contents.RangeTombstones);
        Assert.True(contents.DataBlockCount > 0);
        Assert.Equal(contents.DataBlockCount, reader.DataBlockCount);
        Assert.Equal(SstIndexKind.Trie, SstCodec.GetIndexKind(bytes));
        var decision = reader.GetPointReadDecision(
            "tenant/shared/static-segment/0000"u8);
        Assert.Equal(1, decision.CandidateBlocks);
        Assert.Equal(1, decision.BloomChecks);
    }

    [Fact]
    public void ShouldValidateManifestJournalAndIntentGivenPinnedMidgeMetadataGoldens()
    {
        using var manifest = JsonDocument.Parse(
            ReadStorageFixture("metadata/manifest.snapshot.json"));
        var journal = ReadStorageFixture("metadata/manifest.journal");
        using var intent = JsonDocument.Parse(
            ReadStorageFixture("metadata/intent_log.json"));

        Assert.True(manifest.RootElement.GetProperty("last_persisted_sequence").GetUInt64() > 0);
        Assert.NotEmpty(manifest.RootElement.GetProperty("files").EnumerateArray());
        Assert.NotEmpty(journal);
        LocalDiskStore.ValidateManifestJournal(journal);
        Assert.NotEmpty(intent.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task ShouldParseMidgeLeaseDocumentsGivenPinnedSemanticGoldens()
    {
        var local = ParseLease(ReadStorageFixture("leases/local.midge_leader"));
        var cloudBytes = ReadStorageFixture("leases/cloud.midge_primary_lease");
        var cloud = ParseLease(cloudBytes);

        Assert.Equal("1", local["epoch"]);
        Assert.NotEmpty(local["holder_id"]);
        _ = DateTimeOffset.Parse(local["acquired_at"], CultureInfo.InvariantCulture);
        Assert.Equal("1", cloud["epoch"]);
        Assert.NotEmpty(cloud["owner_token"]);
        _ = DateTimeOffset.Parse(cloud["acquired_at"], CultureInfo.InvariantCulture);
        _ = DateTimeOffset.Parse(cloud["expires_at"], CultureInfo.InvariantCulture);

        var objects = new TestCloudObjectStore();
        _ = await objects.PutAsync(
            PantsCloudObjectLayout.LeaseObjectKey,
            cloudBytes,
            new PantsCloudObjectWriteCondition.Unconditional(),
            CancellationToken.None);
        var store = new CloudObjectLeaseStore(objects, PantsCloudObjectLayout.LeaseObjectKey);
        var snapshot = await store.ReadAsync(CancellationToken.None);
        Assert.Equal(1UL, Assert.IsType<CloudLeaseSnapshot>(snapshot).Lease.Epoch);
    }

    [Fact]
    public void ShouldMatchDdlOperationGivenPinnedMidgeRegistryAndPrepareGoldens()
    {
        var registry = CloudDdlJson.DeserializeRegistry(
            ReadStorageFixture("ddl/ddl.registry.json"));
        var prepare = CloudDdlJson.DeserializePrepare(
            ReadStorageFixture("ddl/ddl.prepare.json"));

        var operation = Assert.Single(registry.Operations);
        Assert.Equal(operation.OperationId, prepare.OperationId);
        Assert.Equal(operation.Edit.GetRawText(), prepare.Edit.GetRawText());
        Assert.Equal("fixture", CloudDdlEdit.GetColumnFamilyName(operation.Edit));
    }

    [Fact]
    public void ShouldDecodePinnedMidgeCloudWalCatalogAndReferencedSegment()
    {
        var catalog = JsonSerializer.Deserialize<ProviderWalCatalog>(
            ReadStorageFixture("cloud/publication-catalog.v1.json"),
            CatalogJsonOptions);

        var decoded = Assert.IsType<ProviderWalCatalog>(catalog);
        Assert.Equal(1U, decoded.FormatVersion);
        Assert.Equal(1UL, decoded.FencingEpoch);
        var segment = Assert.Single(decoded.Segments).Value;
        Assert.Equal(1UL, segment.SegmentId);
        Assert.Equal(1UL, segment.WriterEpoch);
        Assert.Equal(
            "wal/epochs/00000000000000000001/00000000000000000001.wal",
            segment.ObjectKey);
        Assert.True(segment.MaximumSequence > 0);
        Assert.NotEmpty(ReadStorageFixture("wal/sealed/00000000000000000001.wal"));
    }

    [Fact]
    public async Task ShouldVerifyGeneratedMidgeDatabaseOfflineWithoutMutation()
    {
        var path = StorageFixturePath("databases/midge-structured-v4-db");
        var before = ComputeDirectoryHash(path);
        using var descriptor = JsonDocument.Parse(
            ReadStorageFixture("databases/midge-structured-v4-db.fixture.json"));
        var descriptorRoot = descriptor.RootElement;

        Assert.Equal(1, descriptorRoot.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(PinnedMidgeSha, descriptorRoot.GetProperty("midgeSha").GetString());
        Assert.Equal("midge-structured-v4-db", descriptorRoot.GetProperty("path").GetString());
        Assert.Equal(before, descriptorRoot.GetProperty("sha256").GetString());

        var report = await PantsDatabase.VerifyPathAsync(path);

        Assert.Equal(PantsEngineHealth.Healthy, report.Health);
        Assert.True(report.Authoritative);
        Assert.True(report.SstFilesVerified > 0);
        Assert.Equal(before, ComputeDirectoryHash(path));
    }

    static Dictionary<string, string> ParseLease(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Split(": ", 2))
            .ToDictionary(static parts => parts[0], static parts => parts[1], StringComparer.Ordinal);

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
