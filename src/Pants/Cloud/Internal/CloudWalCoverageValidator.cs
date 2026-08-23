namespace Cntryl.Pants;

static class CloudWalCoverageValidator
{
    internal static void ValidateAndEnsureCovered(
        ReadOnlySpan<byte> bytes,
        ulong expectedMaximumSequence,
        ulong expectedWriterEpoch,
        MidgeManifest manifest)
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
        MidgeManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (bytes.IsEmpty)
        {
            throw new PantsCorruptionException("A published cloud WAL segment is empty.");
        }

        var observedMaximumSequence = 0UL;
        ulong? observedWriterEpoch = null;
        var mutations = new List<MidgeWalMutation>();
        try
        {
            MidgeWalFrameReader.Visit(
                bytes,
                (record, _) =>
                {
                    if (observedWriterEpoch.HasValue &&
                        observedWriterEpoch.Value != record.WriterEpoch)
                    {
                        throw new PantsStorageException(
                            "A published cloud WAL segment mixes writer epochs.");
                    }

                    observedWriterEpoch = record.WriterEpoch;
                    observedMaximumSequence = Math.Max(
                        observedMaximumSequence,
                        record.Sequence);
                    if (record.Operation == MidgeWalOperation.TransactionBatch)
                    {
                        mutations.AddRange(MidgeWalCodec.DecodeTransactionBatch(
                            record,
                            out var commitSequence,
                            out var writerEpoch));
                    }
                    else if (MidgeWalCodec.IsMutation(record.Operation))
                    {
                        mutations.Add(MidgeWalCodec.DecodeMutation(record));
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

    static bool Covers(MidgeFileMeta file, MidgeWalMutation mutation)
    {
        if (file.ColumnFamilyId != mutation.ColumnFamilyId ||
            !file.SmallestSequence.HasValue ||
            !file.LargestSequence.HasValue ||
            mutation.Sequence < file.SmallestSequence.Value ||
            mutation.Sequence > file.LargestSequence.Value ||
            file.SmallestKey is null ||
            file.LargestKey is null)
        {
            return false;
        }

        var smallestKey = DecodeManifestKey(file.SmallestKey);
        var largestKey = DecodeManifestKey(file.LargestKey);
        if (mutation.Key.AsSpan().SequenceCompareTo(smallestKey) < 0)
        {
            return false;
        }

        return mutation.Operation == MidgeWalOperation.DeleteRange
            ? mutation.RangeEnd is not null &&
              mutation.RangeEnd.AsSpan().SequenceCompareTo(largestKey) <= 0
            : mutation.Key.AsSpan().SequenceCompareTo(largestKey) <= 0;
    }

    static byte[] DecodeManifestKey(int[] values)
    {
        var bytes = new byte[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] is < byte.MinValue or > byte.MaxValue)
            {
                throw new PantsCorruptionException(
                    "A cloud manifest contains a key byte outside the valid range.");
            }

            bytes[index] = (byte)values[index];
        }

        return bytes;
    }
}
