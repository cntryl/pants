using System.Buffers.Binary;
using System.Text;
using Cntryl.Pants.Compatibility;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class ManifestJournalSalvageTests
{
    const byte CreateColumnFamilyRecordType = 3;
    const byte BatchRecordType = 8;
    const byte DurabilityMarkerRecordType = 9;

    [Fact]
    public async Task ShouldApplyNeitherEditOfDamagedBatchGivenSalvageReplay()
    {
        using var directory = new TemporaryDirectory();
        await WriteEmptyFixtureAsync(directory.Path);
        var journal = EncodeJournalRecord(
                CreateColumnFamilyRecordType,
                """{"edit_id":1,"edit":{"CreateColumnFamily":{"id":1,"name":"committed-family","created_at":1}}}""")
            .Concat(EncodeJournalRecord(DurabilityMarkerRecordType, DurabilityMarkerJson(1)))
            .Concat(EncodeJournalRecord(
                BatchRecordType,
                """
                {"edit_id":2,"edit":{"Batch":[
                  {"CreateColumnFamily":{"id":2,"name":"batched-family","created_at":1}},
                  {"RemoveSst":{"name":"../x.sst"}}
                ]}}
                """))
            .Concat(EncodeJournalRecord(DurabilityMarkerRecordType, DurabilityMarkerJson(2)))
            .ToArray();
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "manifest.journal"), journal);

        await using (var reopened = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path).WithRecoveryPolicy(PantsRecoveryPolicy.Salvage)))
        {
            Assert.Null(await reopened.ColumnFamilies.GetAsync("batched-family"));
            Assert.Null(await reopened.ColumnFamilies.GetAsync("committed-family"));
            Assert.Equal(
                PantsEngineHealth.SalvageMode,
                (await reopened.Diagnostics.GetRuntimeMetricsAsync()).Health);
        }

        await using var recovered = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithRecoveryPolicy(PantsRecoveryPolicy.Salvage));
        Assert.Null(await recovered.ColumnFamilies.GetAsync("batched-family"));
        Assert.Null(await recovered.ColumnFamilies.GetAsync("committed-family"));
    }

    static string DurabilityMarkerJson(ulong sequence) =>
        $"{{\"last_persisted_sequence\":{sequence},\"ts_millis\":1}}";

    static byte[] EncodeJournalRecord(byte recordType, string json)
    {
        var payload = MetadataFrameCodec.Encode(2, Encoding.UTF8.GetBytes(json));
        var record = new byte[1 + sizeof(uint) + payload.Length + sizeof(uint)];
        record[0] = recordType;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(record.AsSpan(5));
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(5 + payload.Length), DiskFormat.Crc32C(payload));
        return record;
    }

    static async Task WriteEmptyFixtureAsync(string path)
    {
        await File.WriteAllTextAsync(
            Path.Combine(path, "FORMAT"),
            "midge-format-version=9\n");
        await Format9TestMetadata.WriteAsync(
            Path.Combine(path, "manifest.snapshot.json"),
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
}
