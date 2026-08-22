using System.Buffers.Binary;

namespace Pants;

static class CloudWalCoverageValidator
{
    internal static void ValidateAndEnsureCovered(
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

        var cursor = 0;
        var observedMaximumSequence = 0UL;
        ulong? observedWriterEpoch = null;
        var mutations = new List<MidgeWalMutation>();
        while (cursor < bytes.Length)
        {
            if (bytes.Length - cursor < 2 * sizeof(uint))
            {
                throw new PantsCorruptionException(
                    "A published cloud WAL segment has a torn frame header.");
            }

            var payloadLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.Slice(cursor, sizeof(uint))));
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.Slice(cursor + sizeof(uint), sizeof(uint)));
            cursor += 2 * sizeof(uint);
            if (payloadLength > MidgeDiskFormat.WalMaximumRecordBytes ||
                payloadLength > bytes.Length - cursor)
            {
                throw new PantsCorruptionException(
                    "A published cloud WAL segment has a torn or oversized frame payload.");
            }

            var payload = bytes.Slice(cursor, payloadLength);
            if (MidgeDiskFormat.Crc32C(payload) != expectedCrc)
            {
                throw new PantsCorruptionException(
                    "A published cloud WAL segment has a corrupt frame checksum.");
            }

            IReadOnlyList<MidgeWalMutation> frameMutations;
            ulong commitSequence;
            ulong writerEpoch;
            try
            {
                frameMutations = MidgeWalCodec.DecodeTransactionBatch(
                    payload,
                    out commitSequence,
                    out writerEpoch);
            }
            catch (PantsException exception)
            {
                throw new PantsCorruptionException(
                    "A published cloud WAL transaction frame is malformed.",
                    exception);
            }

            if (observedWriterEpoch.HasValue && observedWriterEpoch.Value != writerEpoch)
            {
                throw new PantsCorruptionException(
                    "A published cloud WAL segment mixes writer epochs.");
            }

            observedWriterEpoch = writerEpoch;
            observedMaximumSequence = Math.Max(observedMaximumSequence, commitSequence);
            mutations.AddRange(frameMutations);
            cursor += payloadLength;
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

        foreach (var mutation in mutations)
        {
            if (!manifest.Files.Any(file => Covers(file, mutation)))
            {
                throw new PantsCorruptionException(
                    "A published cloud WAL segment contains data not covered by the committed manifest.");
            }
        }
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
