using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Observability;

public sealed class PantsRecoveryJournalWorkTests
{
    [Fact]
    public async Task ShouldReportValidatedJournalFramesAndRecoverEveryFamilyAndFrontier()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var empty = await PantsDatabase.OpenAsync(options))
        {
        }

        using var journal = new MemoryStream();
        for (var id = 1; id <= 70; id++)
        {
            journal.Write(Encode(3, JsonSerializer.Serialize(new
            {
                edit_id = id,
                edit = new { CreateColumnFamily = new { id, name = $"family-{id}", created_at = 1 } }
            })));
            journal.Write(Encode(9, JsonSerializer.Serialize(new { last_persisted_sequence = id, ts_millis = 1 })));
        }
        File.WriteAllBytes(Path.Combine(directory.Path, "manifest.journal"), journal.ToArray());
        var completed = new List<RecoveryWorkCompleted>();
        await using (var recovered = await PantsDatabase.OpenForTestingAsync(options,
                         new RuntimeDependencies(recoveryWorkCompleted: completed.Add)))
        {
            Assert.Equal(71, (await recovered.ColumnFamilies.ListAsync()).Count);
            for (var id = 1; id <= 70; id++)
            {
                var family = Assert.IsAssignableFrom<IPantsColumnFamily>(await recovered.ColumnFamilies.GetAsync($"family-{id}"));
                await using var read = await recovered.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly);
                Assert.Null(await read.GetAsync("absent"u8.ToArray()));
            }
        }

        Assert.Equal(140, completed.Where(static work => work.Stage == "manifest_journal").Sum(static work => work.Frames));
        Assert.Equal(journal.Length, completed.Where(static work => work.Stage == "manifest_journal").Sum(static work => work.Bytes));
        completed.Clear();
        await using var reopened = await PantsDatabase.OpenForTestingAsync(options,
            new RuntimeDependencies(recoveryWorkCompleted: completed.Add));
        Assert.Equal(71, (await reopened.ColumnFamilies.ListAsync()).Count);
        Assert.Equal(71, completed.Where(static work => work.Stage == "family_frontiers").Sum(static work => work.Operations));
    }

    [Fact]
    public async Task ShouldNotFlushPartialJournalWorkFromFailedDecodeOrErrorCleanup()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path);
        await using (var empty = await PantsDatabase.OpenAsync(options))
        {
        }

        var valid = Encode(3, """{"edit_id":1,"edit":{"CreateColumnFamily":{"id":1,"name":"valid","created_at":1}}}""");
        var invalid = Encode(9, """{"last_persisted_sequence":1,"ts_millis":1}""");
        invalid[^1] ^= 1;
        File.WriteAllBytes(Path.Combine(directory.Path, "manifest.journal"), [.. valid, .. invalid]);
        var completed = new List<RecoveryWorkCompleted>();

        await Assert.ThrowsAnyAsync<PantsException>(() => PantsDatabase.OpenForTestingAsync(options,
            new RuntimeDependencies(recoveryWorkCompleted: completed.Add)).AsTask());

        Assert.DoesNotContain(completed, static work => work.Stage == "manifest_journal");
    }

    static byte[] Encode(byte kind, string json)
    {
        var payload = MetadataFrameCodec.Encode(2, Encoding.UTF8.GetBytes(json));
        var bytes = new byte[9 + payload.Length];
        bytes[0] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), checked((uint)payload.Length));
        payload.CopyTo(bytes.AsSpan(5));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(5 + payload.Length), DiskFormat.Crc32C(payload));
        return bytes;
    }
}
