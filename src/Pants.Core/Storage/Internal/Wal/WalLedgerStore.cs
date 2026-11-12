using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Pants.Storage.Internal.Wal;

sealed class WalLedgerStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new WalJsonBytesConverter() }
    };

    readonly string _directory;

    public WalLedgerStore(string directory, Guid holder, WalChainLink anchor,
        List<WalLedgerMember> members, ulong nextSegmentId, ulong reserved)
    {
        _directory = directory;
        Holder = holder;
        Anchor = anchor;
        Members = members;
        NextSegmentId = nextSegmentId;
        ReservedThrough = reserved;
    }

    public Guid Holder { get; }
    public WalChainLink Anchor { get; private set; }
    public List<WalLedgerMember> Members { get; }
    public ulong NextSegmentId { get; private set; }
    public ulong ReservedThrough { get; private set; }
    public WalLedgerMember Active => Members[^1];
    public string ActivePath => Path.Combine(_directory, $"{Active.Open.Id:00000000000000000000}.wal");

    public static WalLedgerStore LoadOrCreate(string directory, ulong epoch, ulong minimumId = 1, WalChainLink? remoteHead = null, ulong sequenceFloor = 0)
    {
        var path = Path.Combine(directory, "LEDGER");
        if (!File.Exists(path))
        {
            if (remoteHead is null && (Directory.EnumerateFiles(directory, "*.wal").Any() || File.Exists(Path.Combine(directory, "wal.log"))))
            {
                throw new PantsCorruptionException("WAL files without a LEDGER are not authoritative history.");
            }

            var anchor = remoteHead ?? WalChainLink.Genesis;
            var firstId = Math.Max(minimumId, checked(anchor.Id + 1));
            var firstSequence = SequenceSpace.FirstAfter(Math.Max(sequenceFloor, anchor.LastSeq));
            var ledger = new WalLedgerStore(directory, Guid.NewGuid(), anchor,
                [new WalLedgerMember(new WalOpenFacts(firstId, epoch, firstSequence, anchor), null, 128)], checked(firstId + 1), Math.Max(sequenceFloor, anchor.LastSeq));
            AtomicStagedFile.Write(ledger.ActivePath, WalSegmentCodec.EncodeHeader(ledger.Active.Open, ledger.Holder));
            ledger.Save();
            return ledger;
        }

        return LoadExisting(directory);
    }

    public static WalLedgerStore LoadExisting(string directory)
    {
        var path = Path.Combine(directory, "LEDGER");
        if (!File.Exists(path))
        {
            throw new PantsCorruptionException("WAL history has no authoritative LEDGER.");
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || !bytes.AsSpan(0, 8).SequenceEqual("MDGLEDGR"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) != 2)
        {
            throw new PantsCompatibilityException("Unsupported WAL LEDGER header.");
        }

        WalLedgerStore? result = null;
        var cursor = 12;
        while (cursor < bytes.Length)
        {
            if (bytes.Length - cursor < 8)
            {
                if (result is null) { throw new PantsCorruptionException("WAL ledger snapshot is incomplete."); }
                break;
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor));
            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor + 4));
            if (length > DiskFormat.WalMaximumRecordBytes || length > bytes.Length - cursor - 8)
            {
                if (result is not null && !HasLaterFrame(bytes, cursor + 1))
                {
                    break;
                }

                throw new PantsCorruptionException("WAL ledger frame length is invalid.");
            }

            var payload = bytes.AsSpan(cursor + 8, (int)length);
            var actual = DiskFormat.Crc32CAppend(DiskFormat.Crc32C(bytes.AsSpan(cursor, 4)), payload);
            if (actual != checksum)
            {
                if (result is not null && !HasLaterFrame(bytes, cursor + 1))
                {
                    break;
                }

                throw new PantsCorruptionException("WAL ledger checksum mismatch.");
            }

            try
            {
                using var document = JsonDocument.Parse(payload.ToArray());
                var root = document.RootElement;
                ValidateUniqueProperties(root);
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1)
                {
                    throw new PantsCorruptionException("WAL ledger frames require exactly one external variant tag.");
                }

                if (result is null)
                {
                    var snapshot = root.GetProperty("Snapshot");
                    var names = new HashSet<string>(StringComparer.Ordinal)
                    { "holder", "anchor", "members", "next_segment_id", "seq_reserved_through" };
                    if (root.EnumerateObject().Count() != 1 || snapshot.EnumerateObject().Any(field => !names.Remove(field.Name)) || names.Count != 0)
                    {
                        throw new PantsCorruptionException("WAL ledger snapshot has unknown or duplicate fields.");
                    }

                    result = new WalLedgerStore(directory, snapshot.GetProperty("holder").GetGuid(),
                        snapshot.GetProperty("anchor").Deserialize<WalChainLink>(JsonOptions) ?? throw new PantsCorruptionException("WAL anchor is missing."),
                        snapshot.GetProperty("members").Deserialize<List<WalLedgerMember>>(JsonOptions) ?? throw new PantsCorruptionException("WAL members are missing."),
                        snapshot.GetProperty("next_segment_id").GetUInt64(),
                        snapshot.GetProperty("seq_reserved_through").GetUInt64());
                }
                else
                {
                    result.Apply(root.GetProperty("Edit"));
                }
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new PantsCorruptionException("WAL ledger payload is invalid.", exception);
            }

            result.Validate();
            cursor += 8 + (int)length;
        }

        return result ?? throw new PantsCorruptionException("WAL ledger has no snapshot.");
    }

    static void ValidateUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject())
            {
                if (!names.Add(field.Name))
                {
                    throw new PantsCorruptionException("WAL ledger payload has duplicate fields.");
                }

                ValidateUniqueProperties(field.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                ValidateUniqueProperties(item);
            }
        }
    }

    static bool HasLaterFrame(ReadOnlySpan<byte> bytes, int cursor)
    {
        for (; cursor <= bytes.Length - 8; cursor++)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[cursor..]);
            if (length == 0 || length > DiskFormat.WalMaximumRecordBytes || length > bytes.Length - cursor - 8)
            {
                continue;
            }

            var payload = bytes.Slice(cursor + 8, (int)length);
            var actual = DiskFormat.Crc32CAppend(DiskFormat.Crc32C(bytes.Slice(cursor, 4)), payload);
            if (actual != BinaryPrimitives.ReadUInt32LittleEndian(bytes[(cursor + 4)..]))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(payload.ToArray());
                if (document.RootElement.TryGetProperty("Edit", out _))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // A byte coincidence is not a verified later ledger record.
            }
        }

        return false;
    }

    void Apply(JsonElement edit)
    {
        var variant = edit.EnumerateObject().Single();
        var fields = variant.Value;
        string[] allowedFields = variant.Name switch
        {
            "Reserve" => ["through"],
            "SyncOpen" => ["id", "through_bytes"],
            "Rotate" => ["sealed", "open"],
            "Reopen" => ["open"],
            "AdvanceLogStart" => ["to"],
            "SalvageOpen" => ["id", "valid_bytes", "floor"],
            "SetAside" => ["from", "cut", "open"],
            "Rebase" => ["anchor", "open"],
            _ => throw new PantsCorruptionException($"Unsupported WAL ledger edit '{variant.Name}'.")
        };
        if (fields.EnumerateObject().Any(field => !allowedFields.Contains(field.Name, StringComparer.Ordinal)))
        {
            throw new PantsCorruptionException("WAL ledger edit has unknown fields.");
        }

        switch (variant.Name)
        {
            case "Reserve":
                var through = fields.GetProperty("through").GetUInt64();
                if (through <= ReservedThrough) { throw new PantsCorruptionException("WAL reservation regressed."); }
                ReservedThrough = through;
                break;
            case "SyncOpen":
                if (fields.GetProperty("id").GetUInt64() != Active.Open.Id) { throw new PantsCorruptionException("WAL sync names another segment."); }
                var boundary = fields.GetProperty("through_bytes").GetUInt64();
                if (boundary < (Active.DurableBytes ?? 128)) { throw new PantsCorruptionException("WAL durable byte boundary regressed."); }
                Members[^1] = Active with { DurableBytes = boundary };
                break;
            case "Rotate":
                Rotate(fields.GetProperty("sealed").Deserialize<WalSealedFacts>(JsonOptions) ?? throw new PantsCorruptionException("WAL sealed facts are missing."),
                    fields.GetProperty("open").Deserialize<WalOpenFacts>(JsonOptions) ?? throw new PantsCorruptionException("WAL open facts are missing."), save: false);
                break;
            case "Reopen":
                var open = fields.GetProperty("open").Deserialize<WalOpenFacts>(JsonOptions) ?? throw new PantsCorruptionException("WAL open facts are missing.");
                ValidateLink(open.Prev);
                if (open.Id != NextSegmentId || !open.Prev.Matches(Active.Open.Prev) || open.Epoch < Active.Open.Epoch)
                { throw new PantsCorruptionException("Invalid WAL reopen edit."); }
                Members[^1] = new WalLedgerMember(open, null, 128);
                NextSegmentId = checked(open.Id + 1);
                break;
            case "AdvanceLogStart":
                var to = fields.GetProperty("to").GetUInt64();
                if (to <= Members[0].Open.Id)
                {
                    throw new PantsCorruptionException("WAL log start must advance.");
                }

                RetireBefore(to, save: false);
                break;
            case "SalvageOpen":
                var salvageId = fields.GetProperty("id").GetUInt64();
                var validBytes = fields.GetProperty("valid_bytes").GetUInt64();
                var floor = fields.GetProperty("floor").GetUInt64();
                if (salvageId != Active.Open.Id || validBytes < WalSegmentCodec.HeaderSize)
                {
                    throw new PantsCorruptionException("Invalid WAL salvage boundary.");
                }

                Members[^1] = Active with { DurableBytes = Math.Min(Active.DurableBytes ?? validBytes, validBytes) };
                ReservedThrough = Math.Max(ReservedThrough, floor);
                break;
            case "SetAside":
                var from = fields.GetProperty("from").GetUInt64();
                var nextOpen = fields.GetProperty("open").Deserialize<WalOpenFacts>(JsonOptions) ?? throw new PantsCorruptionException("WAL open facts are missing.");
                var cut = fields.TryGetProperty("cut", out var cutValue)
                    ? cutValue.Deserialize<WalSealedFacts>(JsonOptions)
                    : null;
                if (from < Members[0].Open.Id || nextOpen.Id < NextSegmentId || nextOpen.Epoch < Active.Open.Epoch)
                {
                    throw new PantsCorruptionException("Invalid WAL set-aside edit.");
                }

                var kept = Members.TakeWhile(member => member.Open.Id < from).ToList();
                if (cut is not null)
                {
                    if (kept.Count == 0 || cut.Link.Id != kept[^1].Open.Id)
                    {
                        throw new PantsCorruptionException("WAL cut does not seal the last kept member.");
                    }

                    kept[^1] = kept[^1] with { Sealed = cut, DurableBytes = Math.Min(kept[^1].DurableBytes ?? cut.DataLen, cut.DataLen) };
                }

                var predecessor = kept.Count == 0 ? Anchor : kept[^1].Sealed?.Link;
                if (predecessor is null || !nextOpen.Prev.Matches(predecessor))
                {
                    throw new PantsCorruptionException("WAL set-aside edit does not preserve its prefix.");
                }

                Members.Clear();
                Members.AddRange(kept);
                Members.Add(new WalLedgerMember(nextOpen, null, 128));
                NextSegmentId = checked(nextOpen.Id + 1);
                break;
            case "Rebase":
                var oldEpoch = Active.Open.Epoch;
                Anchor = fields.GetProperty("anchor").Deserialize<WalChainLink>(JsonOptions) ?? throw new PantsCorruptionException("WAL anchor is missing.");
                var replacement = fields.GetProperty("open").Deserialize<WalOpenFacts>(JsonOptions) ?? throw new PantsCorruptionException("WAL open facts are missing.");
                ValidateLink(Anchor);
                ValidateLink(replacement.Prev);
                if (replacement.Id < NextSegmentId || replacement.Epoch < oldEpoch || !replacement.Prev.Matches(Anchor))
                { throw new PantsCorruptionException("Invalid WAL rebase edit."); }
                Members.Clear();
                Members.Add(new WalLedgerMember(replacement, null, 128));
                NextSegmentId = checked(replacement.Id + 1);
                break;
            default:
                throw new PantsCorruptionException($"Unsupported WAL ledger edit '{variant.Name}'.");
        }
    }

    public void Validate()
    {
        if (Holder == Guid.Empty || Members is null || Members.Count == 0 ||
            Members[^1]?.Open is not { } active || active.Id == ulong.MaxValue ||
            NextSegmentId != active.Id + 1)
        {
            throw new PantsCorruptionException("WAL ledger state is unreachable.");
        }

        ValidateLink(Anchor);
        var link = Anchor;
        var epoch = 0UL;
        for (var index = 0; index < Members.Count; index++)
        {
            var member = Members[index];
            if (member?.Open is not { } open)
            {
                throw new PantsCorruptionException("WAL ledger member is missing header facts.");
            }

            ValidateLink(open.Prev);
            if (!open.Prev.Matches(link) || open.Id <= link.Id || open.FirstSeq <= link.LastSeq ||
                open.Epoch < epoch || (member.Sealed is null) != (index == Members.Count - 1) ||
                member.DurableBytes is < WalSegmentCodec.HeaderSize)
            {
                throw new PantsCorruptionException("WAL ledger member chain is invalid.");
            }

            if (member.Sealed is { } facts)
            {
                ValidateLink(facts.Link);
                if (facts.Link.Id != open.Id || facts.DataLen < WalSegmentCodec.HeaderSize ||
                    member.DurableBytes > facts.DataLen || facts.MaxSeqPerCf is null ||
                    (facts.RecordCount == 0
                        ? facts.Link.LastSeq != open.Prev.LastSeq || facts.MaxSeqPerCf.Length != 0
                        : facts.Link.LastSeq < open.FirstSeq))
                {
                    throw new PantsCorruptionException("WAL sealed facts are invalid.");
                }

                ulong? previousFamily = null;
                foreach (var pair in facts.MaxSeqPerCf)
                {
                    if (pair is not { Length: 2 } || pair[0] > uint.MaxValue ||
                        previousFamily >= pair[0] || pair[1] < open.FirstSeq || pair[1] > facts.Link.LastSeq)
                    {
                        throw new PantsCorruptionException("WAL sealed family maxima are invalid.");
                    }

                    previousFamily = pair[0];
                }

                link = facts.Link;
            }

            epoch = open.Epoch;
        }
    }

    static void ValidateLink(WalChainLink? link)
    {
        if (link?.Digest is not { Length: 16 } ||
            link.Id == 0 && (link.LastSeq != 0 || link.Digest.AsSpan().IndexOfAnyExcept((byte)0) >= 0))
        {
            throw new PantsCorruptionException("WAL chain link is invalid.");
        }
    }

    public void Save()
    {
        Validate();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["Snapshot"] = new
            {
                holder = Holder,
                anchor = Anchor,
                members = Members,
                next_segment_id = NextSegmentId,
                seq_reserved_through = ReservedThrough
            }
        }, JsonOptions);
        var bytes = new byte[20 + payload.Length];
        "MDGLEDGR"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), checked((uint)payload.Length));
        payload.CopyTo(bytes, 20);
        var crc = DiskFormat.Crc32CAppend(DiskFormat.Crc32C(bytes.AsSpan(12, 4)), payload);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), crc);
        AtomicStagedFile.Write(Path.Combine(_directory, "LEDGER"), bytes);
    }

    public void Reopen(WalOpenFacts next)
    {
        if (next.Id != NextSegmentId || !next.Prev.Matches(Active.Open.Prev) || next.Epoch < Active.Open.Epoch)
        {
            throw new PantsCorruptionException("WAL reopen does not preserve the predecessor link.");
        }

        Members[^1] = new WalLedgerMember(next, null, 128);
        NextSegmentId = checked(next.Id + 1);
        Save();
    }

    public void Reserve(ulong through)
    {
        ReservedThrough = Math.Max(through, ReservedThrough);
        Save();
    }

    public void SyncOpen(ulong throughBytes)
    {
        Members[^1] = Active with { DurableBytes = throughBytes };
        Save();
    }

    public void Rotate(WalSealedFacts facts, WalOpenFacts next, bool save = true)
    {
        ValidateLink(facts.Link);
        ValidateLink(next.Prev);
        if (facts.Link.Id != Active.Open.Id || next.Id != NextSegmentId ||
            !next.Prev.Matches(facts.Link) || next.Epoch < Active.Open.Epoch || next.FirstSeq <= facts.Link.LastSeq)
        { throw new PantsCorruptionException("WAL rotation does not follow the active segment."); }
        Members[^1] = Active with { Sealed = facts, DurableBytes = facts.DataLen };
        Members.Add(new WalLedgerMember(next, null, 128));
        NextSegmentId = checked(next.Id + 1);
        if (save) { Save(); }
    }

    public void Rebase(WalChainLink head, ulong epoch, ulong minimumId, ulong sequenceFloor)
    {
        var id = Math.Max(NextSegmentId, Math.Max(minimumId, checked(head.Id + 1)));
        var floor = Math.Max(ReservedThrough, Math.Max(head.LastSeq, sequenceFloor));
        var open = new WalOpenFacts(id, epoch, SequenceSpace.FirstAfter(floor), head);
        var path = Path.Combine(_directory, $"{id:00000000000000000000}.wal");
        AtomicStagedFile.Write(path, WalSegmentCodec.EncodeHeader(open, Holder));
        Anchor = head;
        Members.Clear();
        Members.Add(new WalLedgerMember(open, null, 128));
        NextSegmentId = checked(id + 1);
        ReservedThrough = floor;
        Save();
    }

    public void RetireBefore(ulong id, bool save = true)
    {
        if (id > Active.Open.Id) { throw new PantsCorruptionException("WAL retirement passes the open segment."); }
        while (Members.Count > 1 && Members[0].Open.Id < id)
        {
            Anchor = Members[0].Sealed!.Link;
            Members.RemoveAt(0);
        }
        if (save) { Save(); }
    }
}
