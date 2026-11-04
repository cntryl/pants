using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class WalLedgerValidationTests
{
    [Theory]
    [InlineData("{\"Edit\":{\"Reserve\":{\"through\":1}},\"Snapshot\":{}}")]
    [InlineData("{\"Edit\":{\"Reserve\":{\"through\":1}},\"Edit\":{\"Reserve\":{\"through\":2}}}")]
    [InlineData("{\"Edit\":{\"Reserve\":{\"through\":1,\"unknown\":0}}}")]
    [InlineData("{\"Edit\":{\"Reserve\":{\"through\":1,\"through\":2}}}")]
    public void ShouldRejectAmbiguousOrUnknownLedgerEditFieldsAfterReopen(string edit)
    {
        using var directory = new TemporaryDirectory();
        _ = WalLedgerStore.LoadOrCreate(directory.Path, 1);
        var path = Path.Combine(directory.Path, "LEDGER");
        using (var stream = new FileStream(path, FileMode.Append))
        {
            AppendFrame(stream, System.Text.Encoding.UTF8.GetBytes(edit));
        }

        var corrupted = File.ReadAllBytes(path);
        Assert.Throws<PantsCorruptionException>(() => WalLedgerStore.LoadExisting(directory.Path));
        Assert.Equal(corrupted, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("id")]
    [InlineData("first_seq")]
    public void ShouldRejectMissingRequiredSegmentFactsAfterReopen(string field)
    {
        using var directory = new TemporaryDirectory();
        _ = WalLedgerStore.LoadOrCreate(directory.Path, 1);
        var path = Path.Combine(directory.Path, "LEDGER");
        var bytes = File.ReadAllBytes(path);
        var snapshot = JsonNode.Parse(bytes.AsSpan(20))!["Snapshot"]!;
        snapshot["members"]![0]!["open"]!.AsObject().Remove(field);
        WriteSnapshot(path, snapshot);

        Assert.Throws<PantsCorruptionException>(() => WalLedgerStore.LoadExisting(directory.Path));
    }

    [Fact]
    public void ShouldAcceptOmittedOptionalMemberFactsAfterReopen()
    {
        using var directory = new TemporaryDirectory();
        var ledger = WalLedgerStore.LoadOrCreate(directory.Path, 1);
        var path = Path.Combine(directory.Path, "LEDGER");
        var bytes = File.ReadAllBytes(path);
        var snapshot = JsonNode.Parse(bytes.AsSpan(20))!["Snapshot"]!;
        var member = snapshot["members"]![0]!.AsObject();
        member.Remove("sealed");
        member.Remove("durable_bytes");
        WriteSnapshot(path, snapshot);
        using (var stream = new FileStream(path, FileMode.Append))
        {
            AppendFrame(stream, "{\"Edit\":{\"Reserve\":{\"through\":9}}}"u8);
        }

        var reopened = WalLedgerStore.LoadExisting(directory.Path);
        Assert.Equal(ledger.Active.Open.Id, reopened.Active.Open.Id);
        Assert.Equal(ledger.Active.Open.Epoch, reopened.Active.Open.Epoch);
        Assert.True(ledger.Active.Open.Prev.Matches(reopened.Active.Open.Prev));
        Assert.Equal(9UL, reopened.ReservedThrough);
        Assert.Null(reopened.Active.DurableBytes);
    }

    [Fact]
    public void ShouldRejectDuplicateSegmentFactsAfterReopen()
    {
        using var directory = new TemporaryDirectory();
        _ = WalLedgerStore.LoadOrCreate(directory.Path, 1);
        var path = Path.Combine(directory.Path, "LEDGER");
        var bytes = File.ReadAllBytes(path);
        var payload = System.Text.Encoding.UTF8.GetString(bytes.AsSpan(20))
            .Replace("\"epoch\":1", "\"epoch\":1,\"epoch\":2", StringComparison.Ordinal);
        using (var stream = new FileStream(path, FileMode.Create))
        {
            stream.Write(bytes.AsSpan(0, 12));
            AppendFrame(stream, System.Text.Encoding.UTF8.GetBytes(payload));
        }

        Assert.Throws<PantsCorruptionException>(() => WalLedgerStore.LoadExisting(directory.Path));
    }

    [Fact]
    public void ShouldAcceptSetAsideWithoutOptionalCutAfterReopen()
    {
        using var directory = new TemporaryDirectory();
        var ledger = WalLedgerStore.LoadOrCreate(directory.Path, 1);
        var next = ledger.Active.Open with { Id = ledger.NextSegmentId };
        var edit = new JsonObject
        {
            ["Edit"] = new JsonObject
            {
                ["SetAside"] = new JsonObject
                {
                    ["from"] = ledger.Active.Open.Id,
                    ["open"] = JsonSerializer.SerializeToNode(next, WalLedgerStore.JsonOptions)
                }
            }
        };
        using (var stream = new FileStream(Path.Combine(directory.Path, "LEDGER"), FileMode.Append))
        {
            AppendFrame(stream, JsonSerializer.SerializeToUtf8Bytes(edit));
        }

        var reopened = WalLedgerStore.LoadExisting(directory.Path);
        Assert.Equal(next.Id, Assert.Single(reopened.Members).Open.Id);
        Assert.True(reopened.Anchor.Matches(ledger.Anchor));
    }

    [Theory]
    [InlineData("anchor")]
    [InlineData("members")]
    [InlineData("member")]
    [InlineData("open")]
    [InlineData("previous")]
    [InlineData("digest")]
    public void ShouldRejectNullAuthorityFactsAsCorruption(string field)
    {
        using var directory = new TemporaryDirectory();
        _ = WalLedgerStore.LoadOrCreate(directory.Path, 1);
        var path = Path.Combine(directory.Path, "LEDGER");
        var bytes = File.ReadAllBytes(path);
        var snapshot = JsonNode.Parse(bytes.AsSpan(20))!["Snapshot"]!;
        switch (field)
        {
            case "anchor": snapshot["anchor"] = null; break;
            case "members": snapshot["members"] = null; break;
            case "member": snapshot["members"]![0] = null; break;
            case "open": snapshot["members"]![0]!["open"] = null; break;
            case "previous": snapshot["members"]![0]!["open"]!["prev"] = null; break;
            case "digest": snapshot["members"]![0]!["open"]!["prev"]!["digest"] = null; break;
        }

        WriteSnapshot(path, snapshot);
        var corrupted = File.ReadAllBytes(path);

        Assert.Throws<PantsCorruptionException>(() => WalLedgerStore.LoadExisting(directory.Path));
        Assert.Equal(corrupted, File.ReadAllBytes(path));
    }

    [Fact]
    public void ShouldRejectNonGenesisZeroIdAnchorAsCorruption()
    {
        using var directory = new TemporaryDirectory();
        _ = WalLedgerStore.LoadOrCreate(directory.Path, 1);
        var path = Path.Combine(directory.Path, "LEDGER");
        var bytes = File.ReadAllBytes(path);
        var snapshot = JsonNode.Parse(bytes.AsSpan(20))!["Snapshot"]!;
        snapshot["anchor"]!["last_seq"] = 1;
        WriteSnapshot(path, snapshot);

        Assert.Throws<PantsCorruptionException>(() => WalLedgerStore.LoadExisting(directory.Path));
    }

    static void WriteSnapshot(string path, JsonNode snapshot)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, JsonNode>
        {
            ["Snapshot"] = snapshot
        });
        var bytes = new byte[20 + payload.Length];
        "MDGLEDGR"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), checked((uint)payload.Length));
        payload.CopyTo(bytes, 20);
        var checksum = DiskFormat.Crc32CAppend(DiskFormat.Crc32C(bytes.AsSpan(12, 4)), payload);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), checksum);
        File.WriteAllBytes(path, bytes);
    }

    static void AppendFrame(Stream stream, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
        var checksum = DiskFormat.Crc32CAppend(DiskFormat.Crc32C(header[..4]), payload);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checksum);
        stream.Write(header);
        stream.Write(payload);
    }
}
