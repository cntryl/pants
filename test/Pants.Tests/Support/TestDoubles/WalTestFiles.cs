namespace Cntryl.Pants.Support.TestDoubles;

static class WalTestFiles
{
    public static void ReplaceActiveRecords(string root, IReadOnlyList<byte[]> payloads)
    {
        var ledger = WalLedgerStore.LoadExisting(Path.Combine(root, "wal"));
        using var stream = new MemoryStream();
        stream.Write(WalSegmentCodec.EncodeHeader(ledger.Active.Open, ledger.Holder));
        foreach (var payload in payloads)
        {
            DiskFormat.WriteUInt32(stream, checked((uint)payload.Length));
            DiskFormat.WriteUInt32(stream, DiskFormat.Crc32C(payload));
            stream.Write(payload);
        }

        File.WriteAllBytes(ledger.ActivePath, stream.ToArray());
        ledger.SyncOpen(checked((ulong)stream.Length));
        ledger.Reserve(payloads.Max(static payload => WalCodec.DecodeRecord(payload).Sequence));
    }

    public static string ActivePath(string root) =>
        File.Exists(Path.Combine(root, "wal", "LEDGER"))
            ? WalLedgerStore.LoadExisting(Path.Combine(root, "wal")).ActivePath
            : Path.Combine(root, "wal", "00000000000000000001.wal");
}
