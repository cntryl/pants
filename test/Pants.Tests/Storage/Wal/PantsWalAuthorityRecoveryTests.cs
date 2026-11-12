using System.Buffers.Binary;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class PantsWalAuthorityRecoveryTests
{
    [Theory]
    [InlineData("missing-footer")]
    [InlineData("changed-content")]
    [InlineData("missing-file")]
    public async Task ShouldSalvageOnlyAuthorizedPrefixBeforeInvalidSealedMember(string fault)
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options)) { }
        var walDirectory = Path.Combine(directory.Path, "wal");
        var holder = WalLedgerStore.LoadExisting(walDirectory).Holder;
        var members = new List<WalLedgerMember>();
        var previous = WalChainLink.Genesis;
        for (var id = 1UL; id <= 3; id++)
        {
            var open = new WalOpenFacts(id, 1, id, previous);
            using var stream = new FileStream(Path.Combine(walDirectory, $"{id:00000000000000000000}.wal"), FileMode.Create, FileAccess.ReadWrite);
            stream.Write(WalSegmentCodec.EncodeHeader(open, holder));
            var payload = WalCodec.EncodeRecord(new WalRecord(0, WalOperation.Put,
                TestBytes.FromString($"key-{id}"), "value"u8.ToArray(), id, null, null, null, 1));
            WalCodec.AppendFrame(stream.SafeFileHandle, stream.Length, payload);
            var facts = id == 3 ? null : WalSegmentCodec.Seal(stream);
            members.Add(new WalLedgerMember(open, facts, facts?.DataLen ?? checked((ulong)stream.Length)));
            previous = facts?.Link ?? previous;
        }

        new WalLedgerStore(walDirectory, holder, WalChainLink.Genesis, members, 4, 100).Save();
        var damaged = Path.Combine(walDirectory, "00000000000000000002.wal");
        if (fault == "missing-file")
        {
            File.Delete(damaged);
        }
        else
        {
            using var stream = new FileStream(damaged, FileMode.Open, FileAccess.ReadWrite);
            if (fault == "missing-footer")
            {
                stream.SetLength(checked((long)members[1].Sealed!.DataLen));
            }
            else
            {
                stream.SetLength(WalSegmentCodec.HeaderSize);
                var replacement = WalCodec.EncodeRecord(new WalRecord(0, WalOperation.Put,
                    "key-2"u8.ToArray(), "changed"u8.ToArray(), 2, null, null, null, 1));
                WalCodec.AppendFrame(stream.SafeFileHandle, stream.Length, replacement);
                _ = WalSegmentCodec.Seal(stream);
            }
        }

        await using (var salvaged = await PantsDatabase.OpenAsync(options.WithRecoveryPolicy(PantsRecoveryPolicy.Salvage)))
        {
            await using var reader = await salvaged.Transactions.BeginAsync(salvaged.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
            Assert.NotNull(await reader.GetAsync("key-1"u8.ToArray()));
            Assert.Null(await reader.GetAsync("key-2"u8.ToArray()));
            Assert.Null(await reader.GetAsync("key-3"u8.ToArray()));
            Assert.Equal(PantsEngineHealth.SalvageMode, (await salvaged.Diagnostics.GetRuntimeMetricsAsync()).Health);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var final = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.NotNull(await final.GetAsync("key-1"u8.ToArray()));
        Assert.Null(await final.GetAsync("key-2"u8.ToArray()));
        Assert.Null(await final.GetAsync("key-3"u8.ToArray()));
    }

    [Theory]
    [InlineData(PantsRecoveryPolicy.Strict, false)]
    [InlineData(PantsRecoveryPolicy.Salvage, false)]
    [InlineData(PantsRecoveryPolicy.Strict, true)]
    [InlineData(PantsRecoveryPolicy.Salvage, true)]
    public async Task ShouldFailClosedWithoutFamilyAuthorityForAValidatedWalUnit(PantsRecoveryPolicy policy, bool tornTail)
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options)) { }
        var ledger = WalLedgerStore.LoadExisting(Path.Combine(directory.Path, "wal"));
        var payload = WalCodec.EncodeRecord(new WalRecord(42, WalOperation.Put,
            "unknown"u8.ToArray(), "value"u8.ToArray(), ledger.Active.Open.FirstSeq,
            null, null, null, ledger.Active.Open.Epoch));
        WalTestFiles.ReplaceActiveRecords(directory.Path, [payload]);
        if (tornTail)
        {
            using var tail = new FileStream(ledger.ActivePath, FileMode.Append);
            tail.Write([0x10, 0x20, 0x30]);
        }

        var wal = await File.ReadAllBytesAsync(ledger.ActivePath);
        var ledgerPath = Path.Combine(directory.Path, "wal", "LEDGER");
        var authority = await File.ReadAllBytesAsync(ledgerPath);

        var exception = await Assert.ThrowsAnyAsync<PantsException>(async () =>
        {
            await using var reopened = await PantsDatabase.OpenAsync(options.WithRecoveryPolicy(policy));
        });

        Assert.Equal(PantsErrorCode.Corruption, exception.Code);
        Assert.Equal(wal, await File.ReadAllBytesAsync(ledger.ActivePath));
        Assert.Equal(authority, await File.ReadAllBytesAsync(ledgerPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectBatchReservationSpanOverlappingAuthorizedHistory(bool beforeSegment)
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options)) { }
        var ledger = WalLedgerStore.LoadExisting(Path.Combine(directory.Path, "wal"));
        var first = ledger.Active.Open.FirstSeq;
        var records = new List<byte[]>();
        if (!beforeSegment)
        {
            records.Add(WalCodec.EncodeRecord(new WalRecord(0, WalOperation.Put,
                "prefix"u8.ToArray(), "first"u8.ToArray(), first + 10, null, null, null, ledger.Active.Open.Epoch)));
        }

        records.Add(WalCodec.EncodeTransactionBatch(1, beforeSegment ? first - 1 : first + 9,
            ledger.Active.Open.Epoch,
            [new WalMutation(0, WalOperation.Put, "overlap"u8.ToArray(), "value"u8.ToArray(), 0, null, null)]));
        WalTestFiles.ReplaceActiveRecords(directory.Path, records);
        var wal = await File.ReadAllBytesAsync(ledger.ActivePath);

        await Assert.ThrowsAnyAsync<PantsException>(async () =>
        {
            await using var reopened = await PantsDatabase.OpenAsync(options);
        });

        Assert.Equal(wal, await File.ReadAllBytesAsync(ledger.ActivePath));
    }

    [Theory]
    [InlineData(PantsRecoveryPolicy.Strict)]
    [InlineData(PantsRecoveryPolicy.Salvage)]
    public async Task ShouldRejectUnsupportedWalPayloadWithoutRewritingHistory(PantsRecoveryPolicy policy)
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options)) { }
        var ledger = WalLedgerStore.LoadExisting(Path.Combine(directory.Path, "wal"));
        var payload = WalCodec.EncodeRecord(new WalRecord(0, WalOperation.Put,
            "unsupported"u8.ToArray(), "value"u8.ToArray(), ledger.Active.Open.FirstSeq,
            null, null, null, ledger.Active.Open.Epoch));
        WalTestFiles.ReplaceActiveRecords(directory.Path, [payload]);
        var bytes = await File.ReadAllBytesAsync(ledger.ActivePath);
        var frame = bytes.AsSpan(WalSegmentCodec.HeaderSize);
        frame[10] = 3;
        BinaryPrimitives.WriteUInt32LittleEndian(frame[4..], DiskFormat.Crc32C(frame[8..]));
        await File.WriteAllBytesAsync(ledger.ActivePath, bytes);
        var ledgerPath = Path.Combine(directory.Path, "wal", "LEDGER");
        var authority = await File.ReadAllBytesAsync(ledgerPath);

        await Assert.ThrowsAsync<PantsCompatibilityException>(async () =>
        {
            await using var reopened = await PantsDatabase.OpenAsync(options.WithRecoveryPolicy(policy));
        });

        Assert.Equal(bytes, await File.ReadAllBytesAsync(ledger.ActivePath));
        Assert.Equal(authority, await File.ReadAllBytesAsync(ledgerPath));
    }
}
