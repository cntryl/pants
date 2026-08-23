using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Cntryl.Pants;

internal static class MidgeWalCodec
{
    static ReadOnlySpan<byte> RecordMagic => "MW"u8;

    static ReadOnlySpan<byte> BatchMagic => "TB"u8;

    const int TransactionBatchRecordMinimumLength =
        (4 * sizeof(byte)) + (2 * sizeof(uint)) + sizeof(ulong);

    public static byte[] EncodeRecord(MidgeWalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var payload = new MemoryStream();
        payload.Write(RecordMagic);
        payload.WriteByte(1);
        WriteTlv(payload, 1, [(byte)record.Operation]);
        WriteTlvUInt32(payload, 2, record.ColumnFamilyId);
        WriteTlvUInt64(payload, 3, record.Sequence);
        WriteTlv(payload, 4, record.Key);
        WriteTlvUInt64(payload, 10, record.WriterEpoch);
        if (record.Value is not null)
        {
            var encodedValue = record.Value;
            byte? compression = null;
            if (record.Value.Length >= 256)
            {
                var compressed = MidgeLz4Encoder.CompressWithSizePrefix(record.Value);
                if (compressed.Length < record.Value.Length)
                {
                    encodedValue = compressed;
                    compression = (byte)MidgeCompressionAlgorithm.Lz4;
                }
            }

            WriteTlv(payload, 5, encodedValue);
            if (compression.HasValue)
            {
                WriteTlv(payload, 9, [compression.Value]);
            }
        }

        if (record.Expiration.HasValue)
        {
            WriteTlvUInt64(payload, 6, record.Expiration.Value);
        }

        if (record.RangeEnd is not null)
        {
            WriteTlv(payload, 7, record.RangeEnd);
        }

        if (record.TransactionId.HasValue)
        {
            WriteTlvUInt64(payload, 8, record.TransactionId.Value);
        }

        return payload.ToArray();
    }

    public static MidgeWalRecord DecodeRecord(ReadOnlySpan<byte> payload)
    {
        if (!HasCurrentRecordPrefix(payload))
        {
            throw new PantsStorageException("WAL payload has an invalid Midge record prefix.");
        }

        byte? operation = null;
        uint? columnFamilyId = null;
        ulong? sequence = null;
        byte[]? key = null;
        byte[]? value = null;
        ulong? expiration = null;
        byte[]? rangeEnd = null;
        ulong? transactionId = null;
        ulong? writerEpoch = null;
        byte compression = 0;
        var cursor = 3;
        while (cursor < payload.Length)
        {
            if (payload.Length - cursor < 5)
            {
                throw new PantsStorageException("WAL TLV header is truncated.");
            }

            var tag = payload[cursor++];
            var encodedLength = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
            if (encodedLength > int.MaxValue)
            {
                throw new PantsStorageException("WAL TLV value exceeds the supported size.");
            }

            var length = (int)encodedLength;
            cursor += 4;
            if (length > payload.Length - cursor)
            {
                throw new PantsStorageException("WAL TLV value is truncated.");
            }

            var field = payload.Slice(cursor, length);
            cursor += length;
            switch (tag)
            {
                case 1 when length == 1:
                    operation = field[0];
                    break;
                case 2 when length == sizeof(uint):
                    columnFamilyId = BinaryPrimitives.ReadUInt32LittleEndian(field);
                    break;
                case 3 when length == sizeof(ulong):
                    sequence = BinaryPrimitives.ReadUInt64LittleEndian(field);
                    break;
                case 4:
                    key = field.ToArray();
                    break;
                case 5:
                    value = field.ToArray();
                    break;
                case 6 when length == sizeof(ulong):
                    expiration = BinaryPrimitives.ReadUInt64LittleEndian(field);
                    break;
                case 7:
                    rangeEnd = field.ToArray();
                    break;
                case 8 when length == sizeof(ulong):
                    transactionId = BinaryPrimitives.ReadUInt64LittleEndian(field);
                    break;
                case 9 when length == 1:
                    compression = field[0];
                    break;
                case 10 when length == sizeof(ulong):
                    writerEpoch = BinaryPrimitives.ReadUInt64LittleEndian(field);
                    break;
                case 1 or 2 or 3 or 6 or 8 or 9 or 10:
                    throw new PantsStorageException($"WAL TLV tag '{tag}' has an invalid length.");
            }
        }

        if (operation is null || operation > (byte)MidgeWalOperation.TransactionBatch)
        {
            throw new PantsStorageException("WAL operation is missing or invalid.");
        }

        byte[]? decodedValue;
        try
        {
            decodedValue = value is null
                ? null
                : MidgeDiskFormat.Decompress(value, compression);
        }
        catch (PantsException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new PantsStorageException("WAL value decompression failed.", exception);
        }

        return new MidgeWalRecord(
            columnFamilyId ?? throw new PantsStorageException("WAL column family is missing."),
            (MidgeWalOperation)operation.Value,
            key ?? throw new PantsStorageException("WAL key is missing."),
            decodedValue,
            sequence ?? throw new PantsStorageException("WAL sequence is missing."),
            expiration,
            rangeEnd,
            transactionId,
            writerEpoch ?? throw new PantsStorageException("WAL writer epoch is missing."));
    }

    public static bool HasCurrentRecordPrefix(ReadOnlySpan<byte> payload) =>
        payload.Length >= 3 && payload[..2].SequenceEqual(RecordMagic) && payload[2] == 1;

    public static byte[] EncodeTransactionBatch(
        ulong transactionId,
        ulong beginSequence,
        ulong writerEpoch,
        IReadOnlyList<MidgeWalMutation> mutations)
    {
        if (mutations.Count == 0)
        {
            throw new PantsStorageException("A Midge WAL transaction batch cannot be empty.");
        }

        var commitSequence = AddTransactionSequence(beginSequence, checked((ulong)mutations.Count));
        using var batch = new MemoryStream();
        batch.Write(BatchMagic);
        batch.WriteByte(1);
        MidgeDiskFormat.WriteUInt64(batch, transactionId);
        MidgeDiskFormat.WriteUInt64(batch, beginSequence);
        MidgeDiskFormat.WriteUInt64(batch, commitSequence);
        MidgeDiskFormat.WriteUInt32(batch, checked((uint)mutations.Count));
        for (var index = 0; index < mutations.Count; index++)
        {
            var mutation = mutations[index] with
            {
                Sequence = AddOperationSequence(beginSequence, checked((ulong)index))
            };
            batch.WriteByte((byte)mutation.Operation);
            MidgeDiskFormat.WriteUInt32(batch, mutation.ColumnFamilyId);
            MidgeDiskFormat.WriteUInt64(batch, mutation.Sequence);
            WriteOptionalUInt64(batch, mutation.Expiration);
            WriteBytes(batch, mutation.Key);
            WriteOptionalBytes(batch, mutation.Value);
            WriteOptionalBytes(batch, mutation.RangeEnd);
        }

        return EncodeRecord(new MidgeWalRecord(
            0,
            MidgeWalOperation.TransactionBatch,
            "txn"u8.ToArray(),
            batch.ToArray(),
            commitSequence,
            null,
            null,
            transactionId,
            writerEpoch));
    }

    public static byte[] EncodeTransactionMarker(
        MidgeWalOperation operation,
        ulong transactionId,
        ulong sequence,
        ulong writerEpoch)
    {
        if (operation is not (
                MidgeWalOperation.TransactionBegin or
                MidgeWalOperation.TransactionCommit))
        {
            throw new PantsStorageException(
                $"WAL operation '{operation}' is not a transaction marker.");
        }

        return EncodeRecord(new MidgeWalRecord(
            0,
            operation,
            "txn"u8.ToArray(),
            null,
            sequence,
            null,
            null,
            transactionId,
            writerEpoch));
    }

    public static byte[] EncodeTransactionMutation(
        MidgeWalMutation mutation,
        ulong transactionId,
        ulong writerEpoch) =>
        EncodeRecord(new MidgeWalRecord(
            mutation.ColumnFamilyId,
            mutation.Operation,
            mutation.Key,
            mutation.Value,
            mutation.Sequence,
            mutation.Expiration,
            mutation.RangeEnd,
            transactionId,
            writerEpoch));

    public static IReadOnlyList<MidgeWalMutation> DecodeTransactionBatch(
        ReadOnlySpan<byte> payload,
        out ulong commitSequence) =>
        DecodeTransactionBatch(payload, out commitSequence, out _);

    public static IReadOnlyList<MidgeWalMutation> DecodeTransactionBatch(
        ReadOnlySpan<byte> payload,
        out ulong commitSequence,
        out ulong writerEpoch) =>
        DecodeTransactionBatch(DecodeRecord(payload), out commitSequence, out writerEpoch);

    public static IReadOnlyList<MidgeWalMutation> DecodeTransactionBatch(
        MidgeWalRecord record,
        out ulong commitSequence,
        out ulong writerEpoch)
    {
        if (record.Operation != MidgeWalOperation.TransactionBatch || record.Value is null)
        {
            throw new PantsStorageException("WAL record is not an atomic transaction batch.");
        }

        writerEpoch = record.WriterEpoch;
        var batch = record.Value.AsSpan();
        if (batch.Length < 31 || !batch[..2].SequenceEqual(BatchMagic) || batch[2] != 1)
        {
            throw new PantsStorageException("WAL transaction batch prefix is invalid.");
        }

        var cursor = 3;
        var transactionId = ReadUInt64(batch, ref cursor, "transaction id");
        var beginSequence = ReadUInt64(batch, ref cursor, "begin sequence");
        commitSequence = ReadUInt64(batch, ref cursor, "commit sequence");
        var count = ReadUInt32(batch, ref cursor, "operation count");
        if (count == 0)
        {
            throw new PantsStorageException("WAL transaction batch has an empty operation count.");
        }

        if (count > int.MaxValue)
        {
            throw new PantsStorageException("WAL transaction batch operation count is too large.");
        }

        if (record.TransactionId != transactionId ||
            record.Sequence != commitSequence ||
            commitSequence != AddTransactionSequence(beginSequence, count))
        {
            throw new PantsStorageException("WAL transaction batch metadata is inconsistent.");
        }

        var operationCount = (int)count;
        var remainingBytes = batch.Length - cursor;
        if (operationCount > remainingBytes / TransactionBatchRecordMinimumLength)
        {
            throw new PantsStorageException(
                "WAL transaction batch operation count exceeds remaining bytes.");
        }

        var mutations = new List<MidgeWalMutation>(operationCount);
        for (var index = 0U; index < count; index++)
        {
            var operation = (MidgeWalOperation)ReadByte(batch, ref cursor, "operation");
            if (!IsMutation(operation))
            {
                throw new PantsStorageException(
                    $"WAL transaction batch operation '{operation}' is invalid.");
            }

            var familyId = ReadUInt32(batch, ref cursor, "column family id");
            var sequence = ReadUInt64(batch, ref cursor, "sequence");
            if (sequence != AddOperationSequence(beginSequence, index))
            {
                throw new PantsStorageException(
                    "WAL transaction batch sequences are not contiguous.");
            }

            var expiration = ReadOptionalUInt64(batch, ref cursor, "expiration");
            var key = ReadBytes(batch, ref cursor, "key");
            var value = ReadOptionalBytes(batch, ref cursor, "value");
            var rangeEnd = ReadOptionalBytes(batch, ref cursor, "range end");
            mutations.Add(new MidgeWalMutation(
                familyId,
                operation,
                key,
                value,
                sequence,
                expiration,
                rangeEnd));
        }

        if (cursor != batch.Length)
        {
            throw new PantsStorageException("WAL transaction batch has trailing bytes.");
        }

        return mutations;
    }

    public static void ValidateTransactionBatch(MidgeWalRecord record) =>
        _ = DecodeTransactionBatch(record, out _, out _);

    public static MidgeWalMutation DecodeMutation(MidgeWalRecord record)
    {
        if (!IsMutation(record.Operation))
        {
            throw new PantsStorageException($"WAL operation '{record.Operation}' is not a mutation.");
        }

        if (record.Operation is MidgeWalOperation.Put or MidgeWalOperation.Insert &&
            record.Value is null)
        {
            throw new PantsStorageException("A WAL value write is missing its value.");
        }

        if (record.Operation == MidgeWalOperation.DeleteRange && record.RangeEnd is null)
        {
            throw new PantsStorageException("A WAL range delete is missing its range end.");
        }

        return new MidgeWalMutation(
            record.ColumnFamilyId,
            record.Operation,
            record.Key,
            record.Value,
            record.Sequence,
            record.Expiration,
            record.RangeEnd);
    }

    public static bool TryDecodeMutation(
        MidgeWalRecord record,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MidgeWalMutation? mutation)
    {
        if (!IsMutation(record.Operation) ||
            (record.Operation is MidgeWalOperation.Put or MidgeWalOperation.Insert &&
             record.Value is null) ||
            (record.Operation == MidgeWalOperation.DeleteRange && record.RangeEnd is null))
        {
            mutation = null;
            return false;
        }

        mutation = DecodeMutation(record);
        return true;
    }

    public static void AppendFrame(
        SafeFileHandle handle,
        long offset,
        byte[] payload,
        Action? afterPartialPayload = null)
    {
        if (payload.Length > MidgeDiskFormat.WalMaximumRecordBytes)
        {
            throw new PantsStorageException("WAL record exceeds Midge's 64 MiB frame limit.");
        }

        var header = new byte[2 * sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(sizeof(uint)),
            MidgeDiskFormat.Crc32C(payload));
        if (afterPartialPayload is null)
        {
            RandomAccess.Write(handle, [header, payload], offset);
            return;
        }

        var partialLength = Math.Max(1, payload.Length / 2);
        RandomAccess.Write(handle, [header, payload.AsMemory(0, partialLength)], offset);
        afterPartialPayload();
        RandomAccess.Write(
            handle,
            payload.AsSpan(partialLength),
            checked(offset + header.Length + partialLength));
    }

    public static void AppendFrames(
        SafeFileHandle handle,
        long offset,
        IReadOnlyList<byte[]> payloads,
        Action? afterPartialPayload = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(payloads);
        if (payloads.Count == 0)
        {
            throw new PantsInternalException("A WAL frame group must not be empty.");
        }

        var buffers = new List<ReadOnlyMemory<byte>>(checked(payloads.Count * 2));
        foreach (var payload in payloads)
        {
            if (payload.Length > MidgeDiskFormat.WalMaximumRecordBytes)
            {
                throw new PantsStorageException("WAL record exceeds Midge's 64 MiB frame limit.");
            }

            var header = new byte[2 * sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(
                header.AsSpan(sizeof(uint)),
                MidgeDiskFormat.Crc32C(payload));
            buffers.Add(header);
            buffers.Add(payload);
        }

        if (afterPartialPayload is not null)
        {
            for (var index = 0; index < payloads.Count; index++)
            {
                try
                {
                    afterPartialPayload();
                }
                catch
                {
                    var partialBuffers = buffers
                        .Take(checked(index * 2 + 1))
                        .ToList();
                    var partialLength = Math.Max(1, payloads[index].Length / 2);
                    partialBuffers.Add(payloads[index].AsMemory(0, partialLength));
                    RandomAccess.Write(handle, partialBuffers, offset);
                    throw;
                }
            }
        }

        RandomAccess.Write(handle, buffers, offset);
    }

    public static bool IsMutation(MidgeWalOperation operation) =>
        operation is MidgeWalOperation.Put or
            MidgeWalOperation.Insert or
            MidgeWalOperation.Delete or
            MidgeWalOperation.DeleteRange;

    static void WriteTlv(Stream stream, byte tag, ReadOnlySpan<byte> value)
    {
        stream.WriteByte(tag);
        MidgeDiskFormat.WriteUInt32(stream, checked((uint)value.Length));
        stream.Write(value);
    }

    static void WriteTlvUInt32(Stream stream, byte tag, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        WriteTlv(stream, tag, bytes);
    }

    static void WriteTlvUInt64(Stream stream, byte tag, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        WriteTlv(stream, tag, bytes);
    }

    static void WriteBytes(Stream stream, byte[] value)
    {
        MidgeDiskFormat.WriteUInt32(stream, checked((uint)value.Length));
        stream.Write(value);
    }

    static void WriteOptionalBytes(Stream stream, byte[]? value)
    {
        stream.WriteByte(value is null ? (byte)0 : (byte)1);
        if (value is not null)
        {
            WriteBytes(stream, value);
        }
    }

    static void WriteOptionalUInt64(Stream stream, ulong? value)
    {
        stream.WriteByte(value.HasValue ? (byte)1 : (byte)0);
        if (value.HasValue)
        {
            MidgeDiskFormat.WriteUInt64(stream, value.Value);
        }
    }

    static byte ReadByte(ReadOnlySpan<byte> data, ref int cursor, string field)
    {
        EnsureAvailable(data, cursor, 1, field);
        return data[cursor++];
    }

    static uint ReadUInt32(ReadOnlySpan<byte> data, ref int cursor, string field)
    {
        EnsureAvailable(data, cursor, sizeof(uint), field);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(cursor, sizeof(uint)));
        cursor += sizeof(uint);
        return value;
    }

    static ulong ReadUInt64(ReadOnlySpan<byte> data, ref int cursor, string field)
    {
        EnsureAvailable(data, cursor, sizeof(ulong), field);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(cursor, sizeof(ulong)));
        cursor += sizeof(ulong);
        return value;
    }

    static byte[] ReadBytes(ReadOnlySpan<byte> data, ref int cursor, string field)
    {
        var encodedLength = ReadUInt32(data, ref cursor, field);
        if (encodedLength > int.MaxValue)
        {
            throw new PantsStorageException($"WAL transaction batch {field} exceeds supported limits.");
        }

        var length = (int)encodedLength;
        EnsureAvailable(data, cursor, length, field);
        var value = data.Slice(cursor, length).ToArray();
        cursor += length;
        return value;
    }

    static byte[]? ReadOptionalBytes(ReadOnlySpan<byte> data, ref int cursor, string field) =>
        ReadFlag(data, ref cursor, field) ? ReadBytes(data, ref cursor, field) : null;

    static ulong? ReadOptionalUInt64(ReadOnlySpan<byte> data, ref int cursor, string field) =>
        ReadFlag(data, ref cursor, field) ? ReadUInt64(data, ref cursor, field) : null;

    static bool ReadFlag(ReadOnlySpan<byte> data, ref int cursor, string field)
    {
        var flag = ReadByte(data, ref cursor, field);
        return flag switch
        {
            0 => false,
            1 => true,
            _ => throw new PantsStorageException($"WAL {field} flag is invalid.")
        };
    }

    static void EnsureAvailable(ReadOnlySpan<byte> data, int cursor, int length, string field)
    {
        if (length < 0 || cursor > data.Length - length)
        {
            throw new PantsStorageException($"WAL transaction batch {field} is truncated.");
        }
    }

    static ulong AddTransactionSequence(ulong beginSequence, ulong operationCount)
    {
        if (operationCount == ulong.MaxValue ||
            beginSequence > ulong.MaxValue - operationCount - 1)
        {
            throw new PantsStorageException("WAL transaction sequence range overflows.");
        }

        return beginSequence + operationCount + 1;
    }

    static ulong AddOperationSequence(ulong beginSequence, ulong operationIndex)
    {
        if (operationIndex == ulong.MaxValue ||
            beginSequence > ulong.MaxValue - operationIndex - 1)
        {
            throw new PantsStorageException("WAL operation sequence overflows.");
        }

        return beginSequence + operationIndex + 1;
    }
}
