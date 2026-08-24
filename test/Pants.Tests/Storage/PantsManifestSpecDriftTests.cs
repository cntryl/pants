using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Cntryl.Pants.Tests.Storage;

public sealed class PantsManifestSpecDriftTests
{
    const byte AddSstRecordType = 1;
    const byte RemoveSstRecordType = 2;
    const byte CreateColumnFamilyRecordType = 3;
    const byte SetCloudCheckpointRecordType = 7;
    const byte DurabilityMarkerRecordType = 9;
    const byte DropColumnFamilyAtRecordType = 10;
    const byte ReclaimColumnFamilyRecordType = 11;

    // Issue #47 -----------------------------------------------------------

    [Fact]
    public async Task ShouldTakeMonotonicMaxGivenCloudCheckpointReplayedOutOfOrder()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        var journal = BuildJournal(
            (SetCloudCheckpointRecordType, """{"SetCloudCheckpoint":{"checkpoint_sequence":10,"covering_ssts":[]}}"""),
            (SetCloudCheckpointRecordType, """{"SetCloudCheckpoint":{"checkpoint_sequence":3,"covering_ssts":[]}}"""));
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "manifest.journal"), journal);

        var checkpoint = await ReadCloudCheckpointAfterFlushAsync(directory.Path);

        Assert.Equal(10u, checkpoint.GetProperty("checkpoint_sequence").GetUInt64());
    }

    [Fact]
    public async Task ShouldAdvanceCloudCheckpointGivenAscendingReplayOrder()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        var journal = BuildJournal(
            (SetCloudCheckpointRecordType, """{"SetCloudCheckpoint":{"checkpoint_sequence":3,"covering_ssts":[]}}"""),
            (SetCloudCheckpointRecordType, """{"SetCloudCheckpoint":{"checkpoint_sequence":10,"covering_ssts":[]}}"""));
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "manifest.journal"), journal);

        var checkpoint = await ReadCloudCheckpointAfterFlushAsync(directory.Path);

        Assert.Equal(10u, checkpoint.GetProperty("checkpoint_sequence").GetUInt64());
    }

    static async Task<JsonElement> ReadCloudCheckpointAfterFlushAsync(string path)
    {
        await using (var database = await OpenAsync(path))
        {
            await database.FlushAsync(database.DefaultColumnFamily);
        }

        using var document = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(path, "manifest.json")));
        return document.RootElement.GetProperty("cloud_checkpoint").Clone();
    }

    // Issue #48 -------------------------------------------------------------

    [Fact]
    public async Task ShouldRejectPathTraversalSstNameGivenJournalReplay()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        // RemoveSst never reads SST content and so is not incidentally guarded by any
        // other validation path (e.g. Recover()'s per-file content checks) -- it only
        // becomes safe once ApplyManifestEdit itself validates the name during replay.
        var journal = BuildJournal(
            (2, """{"RemoveSst":{"name":"../escape.sst"}}"""));
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "manifest.journal"), journal);

        var exception = await Assert.ThrowsAnyAsync<PantsException>(() => OpenAsync(directory.Path).AsTask());

        Assert.Equal(PantsErrorCode.RecoveryFailed, exception.Code);
    }

    [Fact]
    public async Task ShouldRejectNulContainingSstNameGivenJournalReplay()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        var journal = BuildJournal(
            (2, "{\"RemoveSst\":{\"name\":\"evil\\u0000.sst\"}}"));
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "manifest.journal"), journal);

        var exception = await Assert.ThrowsAnyAsync<PantsException>(() => OpenAsync(directory.Path).AsTask());

        Assert.Equal(PantsErrorCode.RecoveryFailed, exception.Code);
    }

    [Fact]
    public async Task ShouldFailLoadGivenManifestSnapshotHasDirectlyEmbeddedInvalidSstName()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "FORMAT"), "midge-format-version=3\n");
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "manifest.json"),
            """
            {
              "last_persisted_sequence": 0,
              "files": [
                {
                  "name": "../escape.sst",
                  "level": 0,
                  "size_bytes": 1,
                  "cf_id": 0,
                  "sst_seq": 1
                }
              ],
              "column_families": [],
              "next_wal_seq": 1,
              "next_sst_seqs": {},
              "edit_checkpoint_id": 0
            }
            """);
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "manifest.journal"), []);

        var exception = await Assert.ThrowsAnyAsync<PantsException>(() => OpenAsync(directory.Path).AsTask());

        Assert.Equal(PantsErrorCode.Corruption, exception.Code);
    }

    // Issue #49 -------------------------------------------------------------

    [Fact]
    public async Task ShouldRepairTornJournalTailBeforeNextAppend()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);

        var durableEdit = EncodeJournalRecord(
            CreateColumnFamilyRecordType,
            """{"CreateColumnFamily":{"id":1,"name":"durable-family","created_at":1}}""");
        var durableMarker = EncodeJournalRecord(DurabilityMarkerRecordType, "{}");
        var tornEdit = EncodeJournalRecord(
            CreateColumnFamilyRecordType,
            """{"CreateColumnFamily":{"id":2,"name":"torn-tail-family","created_at":2}}""");
        var tornMarker = EncodeJournalRecord(DurabilityMarkerRecordType, "{}");

        using var stream = new MemoryStream();
        stream.Write(durableEdit);
        stream.Write(durableMarker);
        stream.Write(tornEdit);
        // Simulate a crash mid-append: only part of the durability marker made it to disk.
        stream.Write(tornMarker, 0, tornMarker.Length - 2);
        var journalPath = Path.Combine(directory.Path, "manifest.journal");
        await File.WriteAllBytesAsync(journalPath, stream.ToArray());

        // The normal Open() path ends with an unconditional manifest checkpoint that would
        // itself blank out manifest.journal, masking whether the repair-before-next-append
        // step actually ran. Arm a failpoint that aborts Open() at that final checkpoint
        // (after replay/repair, before the checkpoint touches the journal) so the raw
        // on-disk journal content can be inspected in isolation.
        var failpoint = new ArmableFailpointHandler();
        failpoint.Arm(Failpoint.BeforeManifestCheckpointReplace);
        await Assert.ThrowsAnyAsync<PantsException>(() =>
            PantsDatabase.OpenForTestingAsync(
                PantsOpenOptions.Local(directory.Path),
                new RuntimeDependencies(failpoint)).AsTask());

        var rawJournal = await File.ReadAllBytesAsync(journalPath);
        var rawJournalText = Encoding.UTF8.GetString(rawJournal);

        Assert.DoesNotContain("torn-tail-family", rawJournalText, StringComparison.Ordinal);
        Assert.Contains("durable-family", rawJournalText, StringComparison.Ordinal);

        await using var reopened = await OpenAsync(directory.Path);
        var afterRepair = await reopened.CreateColumnFamilyAsync("after-repair");
        var visibleAfterRepair = Assert.IsAssignableFrom<IPantsColumnFamily>(
            await reopened.GetColumnFamilyAsync("after-repair"));
        Assert.Equal(afterRepair.Id, visibleAfterRepair.Id);
        rawJournalText = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(journalPath));
        Assert.DoesNotContain("torn-tail-family", rawJournalText, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldUseNormativeManifestJournalRepairStagingName()
    {
        using var directory = new TemporaryDirectory();
        var journalPath = Path.Combine(directory.Path, "manifest.journal");
        var repairPath = Path.Combine(directory.Path, "manifest.journal.repair.tmp");
        var sawRepairStagingFile = false;

        AtomicStagedFile.Write(
            journalPath,
            "durable-prefix"u8,
            beforePublish: () => sawRepairStagingFile = File.Exists(repairPath),
            temporaryFileName: "manifest.journal.repair.tmp");

        Assert.True(sawRepairStagingFile);
        Assert.Equal("durable-prefix", File.ReadAllText(journalPath));
        Assert.False(File.Exists(repairPath));
    }

    // Issue #50 -------------------------------------------------------------

    [Fact]
    public async Task ShouldLeaveNextSstSeqsEmptyGivenFreshDatabase()
    {
        using var directory = new TemporaryDirectory();

        await using (var database = await OpenAsync(directory.Path))
        {
            await database.FlushAsync(database.DefaultColumnFamily);
        }

        using var document = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(directory.Path, "manifest.json")));
        var nextSstSeqs = document.RootElement.GetProperty("next_sst_seqs");

        Assert.Equal(JsonValueKind.Object, nextSstSeqs.ValueKind);
        Assert.Empty(nextSstSeqs.EnumerateObject());
    }

    [Fact]
    public async Task ShouldStillAllocateFirstSstSequenceAsOneForFreshColumnFamilyZero()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        await database.FlushAsync(database.DefaultColumnFamily);

        var sstFiles = Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst");
        var sstName = Assert.Single(sstFiles);
        Assert.Contains("00000000000000000001.sst", sstName, StringComparison.Ordinal);
    }

    // Issue #147 ------------------------------------------------------------

    [Fact]
    public async Task ShouldPreserveReclaimedColumnFamilyTombstoneAndNeverReuseItsIdGivenJournalReplay()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        const string reclaimedSst = "000001_00000000000000000001.sst";
        var journal = BuildJournal(
            (CreateColumnFamilyRecordType,
                """{"CreateColumnFamily":{"id":0,"name":"default","created_at":1}}"""),
            (CreateColumnFamilyRecordType,
                """{"CreateColumnFamily":{"id":1,"name":"reclaimed","created_at":2}}"""),
            (AddSstRecordType,
                $"{{\"AddSst\":{{\"name\":\"{reclaimedSst}\",\"level\":0,\"size_bytes\":1,\"cf_id\":1,\"sst_seq\":1,\"sublevel\":0}}}}"),
            (DropColumnFamilyAtRecordType,
                $"{{\"DropColumnFamilyAt\":{{\"id\":1,\"drop_sequence\":3,\"dropped_sst_names\":[\"{reclaimedSst}\"]}}}}"),
            (ReclaimColumnFamilyRecordType,
                $"{{\"ReclaimColumnFamily\":{{\"id\":1,\"names\":[\"{reclaimedSst}\"]}}}}"));
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "manifest.journal"), journal);

        uint subsequentId;
        await using (var database = await OpenAsync(directory.Path))
        {
            subsequentId = (await database.CreateColumnFamilyAsync("subsequent")).Id;
        }

        using var document = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(directory.Path, "manifest.json")));
        var root = document.RootElement;
        Assert.DoesNotContain(
            root.GetProperty("files").EnumerateArray(),
            file => file.GetProperty("name").GetString() == reclaimedSst);
        var tombstone = Assert.Single(
            root.GetProperty("column_families").EnumerateArray(),
            family => family.GetProperty("id").GetUInt32() == 1);
        Assert.True(tombstone.GetProperty("reclaimed").GetBoolean());
        Assert.Empty(tombstone.GetProperty("dropped_sst_names").EnumerateArray());
        Assert.True(subsequentId > 1);
    }

    // Issues #142-#146 ------------------------------------------------------

    [Fact]
    public async Task ShouldSkipJournalEditAlreadyFoldedIntoManifestSnapshot()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "manifest.snapshot.json"),
            """
            {
              "last_persisted_sequence": 0,
              "files": [],
              "column_families": [
                { "id": 0, "name": "default", "created_at": 1 },
                { "id": 1, "name": "current", "created_at": 2 }
              ],
              "next_wal_seq": 1,
              "next_sst_seqs": {},
              "edit_checkpoint_id": 1
            }
            """);
        var staleDrop = BuildEnvelopedJournal(
            (1, DropColumnFamilyAtRecordType,
                """{"DropColumnFamilyAt":{"id":1,"drop_sequence":3,"dropped_sst_names":[]}}"""));
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "manifest.journal"), staleDrop);

        await using (var database = await OpenAsync(directory.Path))
        {
            var family = Assert.IsAssignableFrom<IPantsColumnFamily>(
                await database.GetColumnFamilyAsync("current"));
            Assert.Equal(1u, family.Id);
        }

        using var document = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(directory.Path, "manifest.json")));
        var families = document.RootElement.GetProperty("column_families").EnumerateArray().ToArray();
        Assert.Equal(2, families.Length);
        var current = Assert.Single(families, family => family.GetProperty("id").GetUInt32() == 1);
        Assert.Equal("current", current.GetProperty("name").GetString());
        Assert.False(current.TryGetProperty("deleted_at", out _));
    }

    [Fact]
    public async Task ShouldRejectFullLengthManifestJournalRecordGivenChecksumMismatch()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        var record = EncodeJournalRecord(
            CreateColumnFamilyRecordType,
            """{"CreateColumnFamily":{"id":1,"name":"corrupt","created_at":1}}""");
        record[^1] ^= 0xff;
        await File.WriteAllBytesAsync(
            Path.Combine(directory.Path, "manifest.journal"),
            [.. record, .. EncodeJournalRecord(DurabilityMarkerRecordType, "{}")]);

        var exception = await Assert.ThrowsAnyAsync<PantsException>(() => OpenAsync(directory.Path).AsTask());

        Assert.Equal(PantsErrorCode.RecoveryFailed, exception.Code);
    }

    [Fact]
    public async Task ShouldRejectManifestJournalRecordGivenFramingTypeDisagreesWithEdit()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        var journal = BuildJournal(
            (RemoveSstRecordType,
                """{"CreateColumnFamily":{"id":1,"name":"mismatch","created_at":1}}"""));
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "manifest.journal"), journal);

        var exception = await Assert.ThrowsAnyAsync<PantsException>(() => OpenAsync(directory.Path).AsTask());

        Assert.Equal(PantsErrorCode.RecoveryFailed, exception.Code);
    }

    [Fact]
    public async Task ShouldRejectManifestJournalRecordGivenUnknownRecordType()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        var journal = BuildJournal(
            (12, """{"CreateColumnFamily":{"id":1,"name":"unknown","created_at":1}}"""));
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "manifest.journal"), journal);

        var exception = await Assert.ThrowsAnyAsync<PantsException>(() => OpenAsync(directory.Path).AsTask());

        Assert.Equal(PantsErrorCode.RecoveryFailed, exception.Code);
    }

    [Fact]
    public async Task ShouldResurrectTombstonedColumnFamilyGivenCreateEditReusesItsId()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        var journal = BuildJournal(
            (CreateColumnFamilyRecordType,
                """{"CreateColumnFamily":{"id":0,"name":"default","created_at":1}}"""),
            (CreateColumnFamilyRecordType,
                """{"CreateColumnFamily":{"id":1,"name":"before","created_at":2}}"""),
            (DropColumnFamilyAtRecordType,
                """{"DropColumnFamilyAt":{"id":1,"drop_sequence":3,"dropped_sst_names":[]}}"""),
            (CreateColumnFamilyRecordType,
                """{"CreateColumnFamily":{"id":1,"name":"after","created_at":4}}"""));
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "manifest.journal"), journal);

        await using (var database = await OpenAsync(directory.Path))
        {
            var resurrected = Assert.IsAssignableFrom<IPantsColumnFamily>(
                await database.GetColumnFamilyAsync("after"));
            Assert.Equal(1u, resurrected.Id);
            Assert.Null(await database.GetColumnFamilyAsync("before"));
        }

        using var document = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(directory.Path, "manifest.json")));
        var resurrectedMeta = Assert.Single(
            document.RootElement.GetProperty("column_families").EnumerateArray(),
            family => family.GetProperty("id").GetUInt32() == 1);
        Assert.Equal("after", resurrectedMeta.GetProperty("name").GetString());
        Assert.Equal(4u, resurrectedMeta.GetProperty("created_at").GetUInt64());
        Assert.False(resurrectedMeta.TryGetProperty("deleted_at", out _));
    }

    // Helpers -----------------------------------------------------------------

    static byte[] BuildJournal(params (byte Type, string Json)[] edits)
    {
        using var stream = new MemoryStream();
        foreach (var (type, json) in edits)
        {
            stream.Write(EncodeJournalRecord(type, json));
            stream.Write(EncodeJournalRecord(DurabilityMarkerRecordType, "{}"));
        }

        return stream.ToArray();
    }

    static byte[] BuildEnvelopedJournal(params (ulong Id, byte Type, string Json)[] edits)
    {
        using var stream = new MemoryStream();
        foreach (var (id, type, json) in edits)
        {
            stream.Write(EncodeJournalRecord(type, $"{{\"edit_id\":{id},\"edit\":{json}}}"));
            stream.Write(EncodeJournalRecord(DurabilityMarkerRecordType, "{}"));
        }

        return stream.ToArray();
    }

    static byte[] EncodeJournalRecord(byte recordType, string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var record = new byte[1 + sizeof(uint) + payload.Length + sizeof(uint)];
        record[0] = recordType;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(record.AsSpan(5));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(5 + payload.Length), Crc32(payload));
        return record;
    }

    static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb8_8320;
            }
        }

        return ~crc;
    }

    static async Task WriteEmptyFixtureAsync(string path)
    {
        await File.WriteAllTextAsync(
            Path.Combine(path, "FORMAT"),
            "midge-format-version=3\n");
        await File.WriteAllTextAsync(
            Path.Combine(path, "manifest.json"),
            """
            {
              "last_persisted_sequence": 0,
              "files": [],
              "column_families": [],
              "next_wal_seq": 1,
              "next_sst_seqs": {},
              "edit_checkpoint_id": 0
            }
            """);
        await File.WriteAllBytesAsync(Path.Combine(path, "manifest.journal"), []);
    }

    static ValueTask<IPantsDatabase> OpenAsync(string path) =>
        PantsDatabase.OpenAsync(PantsOpenOptions.Local(path));
}
