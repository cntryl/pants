using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Cntryl.Pants.Tests.Storage;

public sealed class PantsManifestSpecDriftTests
{
    const byte CreateColumnFamilyRecordType = 3;
    const byte SetCloudCheckpointRecordType = 7;
    const byte DurabilityMarkerRecordType = 9;

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
