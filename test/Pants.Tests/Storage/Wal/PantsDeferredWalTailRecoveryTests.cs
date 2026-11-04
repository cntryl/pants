using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class PantsDeferredWalTailRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldValidateDeferredWalBeforePromotingItsPhysicalBytesToDurable(bool completeTail)
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var initial = await PantsDatabase.OpenAsync(options)) { }
        var ledger = WalLedgerStore.LoadExisting(Path.Combine(directory.Path, "wal"));
        var first = ledger.Active.Open.FirstSeq;
        var prefix = WalCodec.EncodeRecord(new WalRecord(0, WalOperation.Put,
            "acknowledged"u8.ToArray(), "prefix"u8.ToArray(), first, null, null, null, ledger.Active.Open.Epoch));
        WalTestFiles.ReplaceActiveRecords(directory.Path, [prefix]);
        ledger = WalLedgerStore.LoadExisting(Path.Combine(directory.Path, "wal"));
        var durablePrefix = ledger.Active.DurableBytes;
        var tail = WalCodec.EncodeRecord(new WalRecord(0, WalOperation.Put,
            "tail"u8.ToArray(), "unacknowledged"u8.ToArray(), first + 1, null, null, null, ledger.Active.Open.Epoch));
        using (var frame = new MemoryStream())
        {
            DiskFormat.WriteUInt32(frame, checked((uint)tail.Length));
            DiskFormat.WriteUInt32(frame, DiskFormat.Crc32C(tail));
            frame.Write(tail);
            using var append = new FileStream(ledger.ActivePath, FileMode.Append);
            append.Write(frame.ToArray().AsSpan(0, completeTail ? checked((int)frame.Length) : 9));
        }
        ledger.Reserve(first + 1);
        var physicalBytes = await File.ReadAllBytesAsync(ledger.ActivePath);
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        using (var store = LocalDiskStore.Open(directory.Path, state, deferWalReplay: true))
        {
            Assert.Equal(durablePrefix, WalLedgerStore.LoadExisting(Path.Combine(directory.Path, "wal")).Active.DurableBytes);
            Assert.Equal(physicalBytes, await File.ReadAllBytesAsync(ledger.ActivePath));
            store.ReplayDeferredWal(state, new StartupPhaseRecorder(null), static _ => { });
            Assert.True(store.CommitSequenceFloor >= first + (completeTail ? 1UL : 0UL));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("prefix"u8.ToArray(), (await reader.GetAsync("acknowledged"u8.ToArray()))?.ToArray());
        var recoveredTail = await reader.GetAsync("tail"u8.ToArray());
        if (completeTail)
        {
            Assert.Equal("unacknowledged"u8.ToArray(), recoveredTail?.ToArray());
        }
        else
        {
            Assert.Null(recoveredTail);
        }
    }

    [Fact]
    public async Task ShouldRefuseActualDurableTailLossBeforeDeferredRecoveryChangesAuthority()
    {
        using var directory = new TemporaryDirectory();
        await using (var initial = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path))) { }
        var ledger = WalLedgerStore.LoadExisting(Path.Combine(directory.Path, "wal"));
        var payload = WalCodec.EncodeRecord(new WalRecord(0, WalOperation.Put,
            "acknowledged"u8.ToArray(), "prefix"u8.ToArray(), ledger.Active.Open.FirstSeq,
            null, null, null, ledger.Active.Open.Epoch));
        WalTestFiles.ReplaceActiveRecords(directory.Path, [payload]);
        using (var truncate = new FileStream(ledger.ActivePath, FileMode.Open, FileAccess.Write))
        {
            truncate.SetLength(truncate.Length - 1);
        }
        var ledgerPath = Path.Combine(directory.Path, "wal", "LEDGER");
        var authority = await File.ReadAllBytesAsync(ledgerPath);
        var physical = await File.ReadAllBytesAsync(ledger.ActivePath);
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());

        _ = Assert.Throws<PantsRecoveryFailedException>(() =>
        {
            using var store = LocalDiskStore.Open(directory.Path, state, deferWalReplay: true);
        });

        Assert.Equal(authority, await File.ReadAllBytesAsync(ledgerPath));
        Assert.Equal(physical, await File.ReadAllBytesAsync(ledger.ActivePath));
    }
}
