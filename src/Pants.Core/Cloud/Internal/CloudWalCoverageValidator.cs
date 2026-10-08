namespace Cntryl.Pants.Cloud.Internal;

static class CloudWalCoverageValidator
{
    internal static void ValidateAndEnsureCovered(
        ReadOnlySpan<byte> bytes,
        ulong expectedMaximumSequence,
        ulong expectedWriterEpoch,
        ManifestState manifest)
    {
        if (!ValidateAndIsCovered(
                bytes,
                expectedMaximumSequence,
                expectedWriterEpoch,
                manifest))
        {
            throw new PantsCorruptionException(
                "A published cloud WAL segment contains data not covered by the committed manifest.");
        }
    }

    internal static bool ValidateAndIsCovered(
        ReadOnlySpan<byte> bytes,
        ulong expectedMaximumSequence,
        ulong expectedWriterEpoch,
        ManifestState manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (bytes.IsEmpty)
        {
            throw new PantsCorruptionException("A published cloud WAL segment is empty.");
        }

        var observedMaximumSequence = 0UL;
        ulong? observedWriterEpoch = null;
        var mutations = new List<WalMutation>();
        try
        {
            WalFrameReader.Visit(
                bytes,
                (record, _) =>
                {
                    if (observedWriterEpoch.HasValue &&
                        observedWriterEpoch.Value != record.WriterEpoch)
                    {
                        throw new StorageException(
                            "A published cloud WAL segment mixes writer epochs.");
                    }

                    observedWriterEpoch = record.WriterEpoch;
                    observedMaximumSequence = Math.Max(
                        observedMaximumSequence,
                        record.Sequence);
                    if (record.Operation == WalOperation.TransactionBatch)
                    {
                        mutations.AddRange(WalCodec.DecodeTransactionBatch(
                            record,
                            out var commitSequence,
                            out var writerEpoch));
                    }
                    else if (WalCodec.IsMutation(record.Operation))
                    {
                        mutations.Add(WalCodec.DecodeMutation(record));
                    }
                });
        }
        catch (PantsException exception) when (exception is not PantsCorruptionException)
        {
            throw new PantsCorruptionException(
                "A published cloud WAL transaction frame is malformed.",
                exception);
        }

        if (observedMaximumSequence != expectedMaximumSequence)
        {
            throw new PantsCorruptionException(
                "A published cloud WAL segment maximum sequence differs from its catalog entry.");
        }

        if (observedWriterEpoch != expectedWriterEpoch)
        {
            throw new PantsCorruptionException(
                "A published cloud WAL segment writer epoch differs from its catalog entry.");
        }

        return mutations.All(mutation => manifest.Files.Any(file => Covers(file, mutation)));
    }

    /// <summary>
    ///     Streaming counterpart of <see cref="ValidateAndIsCovered" />: reads one frame at a time so
    ///     memory is independent of segment size, and records which manifest files cover the
    ///     segment so only those SSTs need a dependency check.
    /// </summary>
    /// <returns>Whether every mutation is covered by some manifest file.</returns>
    internal static bool ValidateStreamAndCollectCoveringFiles(
        Stream stream,
        ulong expectedMaximumSequence,
        ulong expectedWriterEpoch,
        ManifestState manifest,
        ISet<string> coveringFileNames)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(coveringFileNames);
        if (stream.Length == 0)
        {
            throw new PantsCorruptionException("A published cloud WAL segment is empty.");
        }

        var observedMaximumSequence = 0UL;
        ulong? observedWriterEpoch = null;
        var covered = true;
        try
        {
            WalFrameReader.Visit(
                stream,
                (record, frameBytes) =>
                {
                    if (observedWriterEpoch.HasValue &&
                        observedWriterEpoch.Value != record.WriterEpoch)
                    {
                        throw new StorageException(
                            "A published cloud WAL segment mixes writer epochs.");
                    }

                    observedWriterEpoch = record.WriterEpoch;
                    observedMaximumSequence = Math.Max(
                        observedMaximumSequence,
                        record.Sequence);
                    IReadOnlyList<WalMutation> mutations;
                    if (record.Operation == WalOperation.TransactionBatch)
                    {
                        mutations = WalCodec.DecodeTransactionBatch(
                            record,
                            out _,
                            out _);
                    }
                    else if (WalCodec.IsMutation(record.Operation))
                    {
                        mutations = [WalCodec.DecodeMutation(record)];
                    }
                    else
                    {
                        return;
                    }

                    foreach (var mutation in mutations)
                    {
                        var file = manifest.Files.FirstOrDefault(candidate => Covers(candidate, mutation));
                        if (file is null)
                        {
                            covered = false;
                        }
                        else
                        {
                            _ = coveringFileNames.Add(file.Name);
                        }
                    }
                });
        }
        catch (PantsException exception) when (exception is not PantsCorruptionException)
        {
            throw new PantsCorruptionException(
                "A published cloud WAL transaction frame is malformed.",
                exception);
        }

        if (observedMaximumSequence != expectedMaximumSequence)
        {
            throw new PantsCorruptionException(
                "A published cloud WAL segment maximum sequence differs from its catalog entry.");
        }

        if (observedWriterEpoch != expectedWriterEpoch)
        {
            throw new PantsCorruptionException(
                "A published cloud WAL segment writer epoch differs from its catalog entry.");
        }

        return covered;
    }

    static bool Covers(FileMeta file, WalMutation mutation)
    {
        if (file.ColumnFamilyId != mutation.ColumnFamilyId ||
            !file.HasTrustedKeyBounds() ||
            !file.SmallestSequence.HasValue ||
            !file.LargestSequence.HasValue ||
            mutation.Sequence < file.SmallestSequence.Value ||
            mutation.Sequence > file.LargestSequence.Value ||
            file.SmallestKey is null ||
            file.LargestKey is null)
        {
            return false;
        }

        var smallestKey = file.SmallestKey;
        var largestKey = file.LargestKey;
        if (mutation.Key.AsSpan().SequenceCompareTo(smallestKey) < 0)
        {
            return false;
        }

        return mutation.Operation == WalOperation.DeleteRange
            ? mutation.RangeEnd is not null &&
              mutation.RangeEnd.AsSpan().SequenceCompareTo(largestKey) <= 0
            : mutation.Key.AsSpan().SequenceCompareTo(largestKey) <= 0;
    }

}
