namespace Cntryl.Pants.Cloud.Internal.WalRetirement;

/// <summary>
///     Process-local, resumable proof that one published WAL segment is covered by the published
///     manifest. It advances one acknowledged frame at a time: the read offset, the running content
///     checksum, the observed sequence and writer epoch, and the manifest files that cover the
///     frames read so far. It is pinned to one object version; any other version restarts it.
/// </summary>
sealed class WalSegmentProof(ProviderPublishedWalSegment segment, string version)
{
    readonly Dictionary<string, FileMeta> _coveringFiles = new(StringComparer.Ordinal);

    public ProviderPublishedWalSegment Segment { get; } = segment;

    /// <summary>The object version every read of this proof must observe.</summary>
    public string Version { get; } = version;

    /// <summary>The start of the next unacknowledged frame.</summary>
    public long Offset { get; private set; }

    public long Length => checked((long)Segment.SizeBytes);

    public long Remaining => Length - Offset;

    public bool IsRead => Offset == Length;

    public IReadOnlyDictionary<string, FileMeta> CoveringFiles => _coveringFiles;

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

        var covering = new FileMeta[mutations.Count];
        for (var index = 0; index < mutations.Count; index++)
        {
            if (coverage.FindCovering(mutations[index]) is not { } file)
            {
                return false;
            }

            covering[index] = file;
        }

        ObservedWriterEpoch = record.WriterEpoch;
        ObservedMaximumSequence = Math.Max(ObservedMaximumSequence, record.Sequence);
        Checksum = DiskFormat.Crc32CAppend(Checksum, frame);
        Offset = checked(Offset + frame.Length);
        UncoveredUnder = null;
        foreach (var file in covering)
        {
            _coveringFiles[file.Name] = file;
        }

        return true;
    }

    /// <summary>Checks a fully read segment against its catalog entry.</summary>
    public void EnsureMatchesCatalog()
    {
        if (Length == 0)
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
