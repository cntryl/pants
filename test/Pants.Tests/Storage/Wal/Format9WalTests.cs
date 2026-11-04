using System.Buffers.Binary;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class Format9WalTests
{
    [Fact]
    public async Task ShouldRecoverLinkedHistoryAndReserveSequencesAcrossReopen()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        ulong reservation;
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await PutAsync(database, "first", "one");
            reservation = WalLedgerStore.LoadExisting(Path.Combine(directory.Path, "wal")).ReservedThrough;
        }

        await using (var reopened = await PantsDatabase.OpenAsync(options))
        {
            await PutAsync(reopened, "second", "two");
            var ledger = WalLedgerStore.LoadExisting(Path.Combine(directory.Path, "wal"));
            Assert.True(ledger.ReservedThrough > reservation);
            Assert.True(ledger.Active.Open.FirstSeq > 1);
        }

        await using var final = await PantsDatabase.OpenAsync(options);
        await using var reader = await final.Transactions.BeginAsync(final.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("one", TestBytes.ToText((await reader.GetAsync("first"u8.ToArray()))!.Value));
        Assert.Equal("two", TestBytes.ToText((await reader.GetAsync("second"u8.ToArray()))!.Value));
    }

    [Fact]
    public async Task ShouldIgnoreUnlistedWalObjectWithoutReplayingOrDeletingIt()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await PutAsync(database, "kept", "one");
        }

        var orphan = Path.Combine(directory.Path, "wal", "00000000000000999999.wal");
        var bytes = new byte[] { 0xff, 0x01, 0x02 };
        await File.WriteAllBytesAsync(orphan, bytes);
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("one", TestBytes.ToText((await reader.GetAsync("kept"u8.ToArray()))!.Value));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(orphan));
    }

    [Fact]
    public void ShouldValidateSegmentFooterAgainstActualRecordsAndContentDigest()
    {
        using var directory = new TemporaryDirectory();
        var ledger = WalLedgerStore.LoadOrCreate(directory.Path, 7);
        byte[] bytes;
        using (var stream = new FileStream(ledger.ActivePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var payload = WalCodec.EncodeRecord(new WalRecord(0, WalOperation.Put, "key"u8.ToArray(), "value"u8.ToArray(), 1, null, null, null, 7));
            WalCodec.AppendFrame(stream.SafeFileHandle, stream.Length, payload);
            var facts = WalSegmentCodec.Seal(stream);
            Assert.Equal(1UL, facts.RecordCount);
            stream.Position = 0;
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }

        var records = new List<WalRecord>();
        WalFrameReader.Visit(bytes, (record, _) => records.Add(record));
        Assert.Equal(7UL, Assert.Single(records).WriterEpoch);
        var footer = WalSegmentCodec.HeaderSize;
        footer += checked(8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(footer)));
        bytes[^1] ^= 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(footer + 4), DiskFormat.Crc32C(bytes.AsSpan(footer + 8)));
        Assert.Throws<PantsCorruptionException>(() => WalFrameReader.Visit(bytes, static (_, _) => { }));
    }

    [Fact]
    public void ShouldRejectLedgerSnapshotChecksumDamage()
    {
        using var directory = new TemporaryDirectory();
        _ = WalLedgerStore.LoadOrCreate(directory.Path, 1);
        var path = Path.Combine(directory.Path, "LEDGER");
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes);
        Assert.Throws<PantsCorruptionException>(() => WalLedgerStore.LoadExisting(directory.Path));
    }

    [Fact]
    public async Task ShouldKeepSalvagedCommittedPrefixDurableAfterAnotherRestart()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await PutAsync(database, "kept", "one");
            await PutAsync(database, "lost", "two");
        }

        var path = WalTestFiles.ActivePath(directory.Path);
        var bytes = await File.ReadAllBytesAsync(path);
        var second = WalSegmentCodec.HeaderSize + 8 + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(WalSegmentCodec.HeaderSize)));
        bytes[second + 4] ^= 1;
        await File.WriteAllBytesAsync(path, bytes);
        await using (var salvaged = await PantsDatabase.OpenAsync(options.WithRecoveryPolicy(PantsRecoveryPolicy.Salvage)))
        {
            await using var reader = await salvaged.Transactions.BeginAsync(salvaged.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
            Assert.Equal("one", TestBytes.ToText((await reader.GetAsync("kept"u8.ToArray()))!.Value));
            Assert.Null(await reader.GetAsync("lost"u8.ToArray()));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var finalReader = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("one", TestBytes.ToText((await finalReader.GetAsync("kept"u8.ToArray()))!.Value));
        Assert.Null(await finalReader.GetAsync("lost"u8.ToArray()));
    }

    static async Task PutAsync(IPantsDatabase database, string key, string value)
    {
        await using var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }
}
