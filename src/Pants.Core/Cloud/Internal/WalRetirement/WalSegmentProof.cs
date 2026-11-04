using System.Buffers.Binary;
using System.IO.Hashing;

namespace Cntryl.Pants.Cloud.Internal.WalRetirement;

/// <summary>
///     Process-local, resumable proof that one published WAL segment is covered by the published
///     manifest. It advances one acknowledged frame at a time: the read offset, the running content
///     checksum, the observed sequence and writer epoch, and the manifest files that cover the
///     frames read so far. It is pinned to one object version; any other version restarts it.
/// </summary>
sealed class WalSegmentProof(ProviderPublishedWalSegment segment, string version)
{
    readonly XxHash128 _digest = new();
    readonly SortedDictionary<uint, ulong> _familyMaxima = [];
    ulong _recordCount;
    ulong _firstSequence;
    WalOpenFacts? _header;
    bool _footerSeen;
    readonly Dictionary<string, FileMeta> _coveringFiles = new(StringComparer.Ordinal);
    readonly Dictionary<uint, ulong> _coveringDroppedFamilies = [];

    public ProviderPublishedWalSegment Segment { get; } = segment;

    /// <summary>The object version every read of this proof must observe.</summary>
    public string Version { get; } = version;

    /// <summary>The start of the next unacknowledged frame.</summary>
    public long Offset { get; private set; }

    public long Length => checked((long)Segment.SizeBytes);

    public long Remaining => Length - Offset;

    public bool IsRead => Offset == Length;

    public IReadOnlyDictionary<string, FileMeta> CoveringFiles => _coveringFiles;

    public IReadOnlyDictionary<uint, ulong> CoveringDroppedFamilies => _coveringDroppedFamilies;

    /// <summary>
    ///     The manifest under which the frame at <see cref="Offset" /> was found uncovered, or null.
    ///     The proof does not advance past that frame; a newer manifest may cover it.
    /// </summary>
    public PublishedManifestIdentity? UncoveredUnder { get; set; }

    uint Checksum { get; set; }

    ulong ObservedMaximumSequence { get; set; }

    ulong? ObservedWriterEpoch { get; set; }

    /// <summary>
    ///     Acknowledges one checksummed frame when every mutation in it is covered; otherwise leaves
    ///     the proof unchanged.
    /// </summary>
    /// <param name="frame">The whole frame, header and payload.</param>
    /// <returns>Whether the frame was covered and acknowledged.</returns>
    public void AcknowledgeHeader(ReadOnlySpan<byte> bytes)
    {
        var (header, _) = WalSegmentCodec.DecodeHeader(bytes);
        if (Offset != 0 || header.Id != Segment.SegmentId || header.Epoch != Segment.WriterEpoch ||
            Segment.Previous is null || !header.Prev.Matches(Segment.Previous))
        {
            throw new PantsCorruptionException("Published WAL header differs from its catalog.");
        }

        _header = header;
        Checksum = DiskFormat.Crc32CAppend(Checksum, bytes);
        _digest.Append(bytes);
        Offset = bytes.Length;
    }

    public void AcknowledgeFooter(ReadOnlySpan<byte> frame)
    {
        var payload = frame[8..];
        var facts = Segment.Facts ?? throw new PantsCorruptionException("WAL catalog has no sealed facts.");
        var digest = new byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(digest, _digest.GetCurrentHashAsUInt128());
        if (_header is null || _footerSeen || Offset != (long)facts.DataLen || frame.Length != Remaining ||
            payload.Length != 63 + _familyMaxima.Count * 12 || !WalSegmentCodec.IsFooter(payload) || payload[2] != 1 ||
            BinaryPrimitives.ReadUInt64LittleEndian(payload[3..]) != Segment.SegmentId ||
            BinaryPrimitives.ReadUInt64LittleEndian(payload[11..]) != _recordCount ||
            BinaryPrimitives.ReadUInt64LittleEndian(payload[19..]) != _firstSequence ||
            BinaryPrimitives.ReadUInt64LittleEndian(payload[27..]) != ObservedMaximumSequence ||
            BinaryPrimitives.ReadUInt64LittleEndian(payload[35..]) != (ulong)Offset ||
            BinaryPrimitives.ReadUInt32LittleEndian(payload[43..]) != _familyMaxima.Count ||
            !payload[^16..].SequenceEqual(digest) || !facts.Link.Digest.AsSpan().SequenceEqual(digest) ||
            facts.RecordCount != _recordCount || facts.MaxSeqPerCf.Length != _familyMaxima.Count)
        {
            throw new PantsCorruptionException("Published WAL footer disagrees with its records or catalog.");
        }

        var index = 0;
        foreach (var (family, maximum) in _familyMaxima)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(payload[(47 + index * 12)..]) != family ||
                BinaryPrimitives.ReadUInt64LittleEndian(payload[(51 + index * 12)..]) != maximum ||
                facts.MaxSeqPerCf[index].Length != 2 || facts.MaxSeqPerCf[index][0] != family || facts.MaxSeqPerCf[index][1] != maximum)
            {
                throw new PantsCorruptionException("WAL footer family coverage differs from its records.");
            }

            index++;
        }

        Checksum = DiskFormat.Crc32CAppend(Checksum, frame);
        Offset += frame.Length;
        _footerSeen = true;
    }

    public bool TryAcknowledge(ReadOnlySpan<byte> frame, WalRecord record, WalCoverageIndex coverage)
    {
        if (ObservedWriterEpoch is { } epoch && epoch != record.WriterEpoch)
        {
            throw new PantsCorruptionException("A published cloud WAL segment mixes writer epochs.");
        }

        IReadOnlyList<WalMutation> mutations;
        try
        {
            mutations = record.Operation == WalOperation.TransactionBatch
                ? WalCodec.DecodeTransactionBatch(record, out _, out _)
                : WalCodec.IsMutation(record.Operation)
                    ? [WalCodec.DecodeMutation(record)]
                    : [];
        }
        catch (PantsException exception) when (exception is not PantsCorruptionException)
        {
            throw new PantsCorruptionException(
                "A published cloud WAL transaction frame is malformed.",
                exception);
        }

        var coveringFiles = new List<FileMeta>(mutations.Count);
        var coveringDroppedFamilies = new Dictionary<uint, ulong>();
        for (var index = 0; index < mutations.Count; index++)
        {
            if (!coverage.TryFindCovering(
                    mutations[index],
                    out var file,
                    out var droppedFamilyDropSequence))
            {
                return false;
            }

            if (file is not null)
            {
                coveringFiles.Add(file);
            }
            else if (droppedFamilyDropSequence is { } dropSequence)
            {
                coveringDroppedFamilies[mutations[index].ColumnFamilyId] = dropSequence;
            }
            else
            {
                return false;
            }
        }

        if (_header is null || record.Sequence < _header.FirstSeq ||
            _recordCount != 0 && record.Sequence <= ObservedMaximumSequence)
        {
            throw new PantsCorruptionException("WAL record sequences do not follow the segment header.");
        }

        if (_recordCount == 0)
        {
            _firstSequence = record.Sequence;
        }

        _recordCount++;
        _familyMaxima[record.ColumnFamilyId] = record.Sequence;
        _digest.Append(frame);
        ObservedWriterEpoch = record.WriterEpoch;
        ObservedMaximumSequence = Math.Max(ObservedMaximumSequence, record.Sequence);
        Checksum = DiskFormat.Crc32CAppend(Checksum, frame);
        Offset = checked(Offset + frame.Length);
        UncoveredUnder = null;
        foreach (var file in coveringFiles)
        {
            _coveringFiles[file.Name] = file;
        }

        foreach (var (familyId, dropSequence) in coveringDroppedFamilies)
        {
            _coveringDroppedFamilies[familyId] = dropSequence;
        }

        return true;
    }

    /// <summary>Checks a fully read segment against its catalog entry.</summary>
    public void EnsureMatchesCatalog()
    {
        if (Length == 0 || !_footerSeen)
        {
            throw new PantsCorruptionException("A published cloud WAL segment is empty.");
        }

        if (Checksum != Segment.ContentCrc32C)
        {
            throw new PantsCorruptionException(
                $"Published cloud WAL object '{Segment.ObjectKey}' differs from its catalog proof.");
        }

        if (ObservedMaximumSequence != Segment.MaximumSequence)
        {
            throw new PantsCorruptionException(
                "A published cloud WAL segment maximum sequence differs from its catalog entry.");
        }

        if (ObservedWriterEpoch != Segment.WriterEpoch)
        {
            throw new PantsCorruptionException(
                "A published cloud WAL segment writer epoch differs from its catalog entry.");
        }
    }
}
