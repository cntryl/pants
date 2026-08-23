using System.Buffers.Binary;

namespace Pants.Tests;

internal static class TransactionSpillHardeningTestHarness
{
    const int DefaultPoolBytes = 1_024;
    const int DefaultValueBytes = 900;

    internal static ValueTask<IPantsDatabase> OpenLocalAsync(
        string path,
        long transactionMemoryPoolBytes = DefaultPoolBytes) =>
        PantsDatabase.OpenAsync(CreateLocalOptions(path, transactionMemoryPoolBytes));

    internal static ValueTask<IPantsDatabase> OpenLocalForTestingAsync(
        string path,
        IPantsFailpointHandler failpoints,
        long transactionMemoryPoolBytes = DefaultPoolBytes) =>
        PantsDatabase.OpenForTestingAsync(
            CreateLocalOptions(path, transactionMemoryPoolBytes),
            new PantsRuntimeDependencies(failpoints));

    internal static PantsOpenOptions CreateLocalOptions(
        string path,
        long transactionMemoryPoolBytes = DefaultPoolBytes) =>
        PantsOpenOptions.Local(path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(64 * 1_024))
            .WithMemtableLimits(24 * 1_024)
            .WithTransactionMemoryPool(transactionMemoryPoolBytes)
            .WithBackgroundCompaction(false);

    internal static PantsOpenOptions CreateMemoryOptions() =>
        PantsOpenOptions.InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(64 * 1_024 * 1_024))
            .WithTransactionMemoryPool(8 * 1_024);

    internal static PantsFailpoint GetRequiredFailpoint(string name)
    {
        Assert.True(
            Enum.TryParse(name, out PantsFailpoint failpoint),
            $"Pants does not expose the Midge-equivalent '{name}' persistence boundary.");
        return failpoint;
    }

    internal static void Fill(
        IPantsTransaction transaction,
        string prefix,
        int count,
        int valueBytes = DefaultValueBytes)
    {
        var value = Enumerable.Repeat((byte)'x', valueBytes).ToArray();
        for (var index = 0; index < count; index++)
        {
            transaction.Put(TestBytes.FromString($"{prefix}-{index:000}"), value);
        }
    }

    internal static string[] FindArtifacts(string path) =>
        Directory.Exists(Path.Combine(path, "txn"))
            ? Directory.GetFiles(Path.Combine(path, "txn"), "*", SearchOption.AllDirectories)
            : [];

    internal static async ValueTask<string?> ReadTextAsync(
        IPantsDatabase database,
        string key)
    {
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(TestBytes.FromString(key));
        return value is null ? null : TestBytes.ToText(value.Value);
    }

    internal static IReadOnlyList<MidgeWalTestRecord> ReadWalFrames(
        string databasePath)
    {
        var path = Path.Combine(databasePath, "wal", "wal.log");
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var frames = new List<MidgeWalTestRecord>();
        Span<byte> header = stackalloc byte[8];
        while (stream.Position < stream.Length)
        {
            Assert.True(
                stream.Length - stream.Position >= header.Length,
                "The WAL frame header is truncated.");
            stream.ReadExactly(header);
            var encodedPayloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
            Assert.True(
                encodedPayloadLength <= MidgeDiskFormat.WalMaximumRecordBytes,
                "The WAL frame exceeds Midge's 64 MiB limit.");
            var payloadLength = checked((int)encodedPayloadLength);
            var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var payload = GC.AllocateUninitializedArray<byte>(payloadLength);
            stream.ReadExactly(payload);
            Assert.Equal(expectedChecksum, MidgeDiskFormat.Crc32C(payload));
            frames.Add(DecodeWalRecord(payload, payloadLength));
        }

        return frames;
    }

    static MidgeWalTestRecord DecodeWalRecord(ReadOnlySpan<byte> payload, int payloadLength)
    {
        Assert.True(
            payload.Length >= 3 && payload[..2].SequenceEqual("MW"u8) && payload[2] == 1,
            "The WAL payload must use Midge's version-one record envelope.");
        byte? operation = null;
        uint? columnFamilyId = null;
        ulong? sequence = null;
        byte[]? key = null;
        byte[]? value = null;
        ulong? expiration = null;
        byte[]? rangeEnd = null;
        ulong? transactionId = null;
        ulong? writerEpoch = null;
        byte? compression = null;
        var cursor = 3;
        while (cursor < payload.Length)
        {
            Assert.True(payload.Length - cursor >= 5, "The WAL TLV header is truncated.");
            var tag = payload[cursor++];
            var encodedLength = BinaryPrimitives.ReadUInt32LittleEndian(payload[cursor..]);
            cursor += sizeof(uint);
            Assert.True(encodedLength <= int.MaxValue, "The WAL TLV length exceeds the platform limit.");
            var length = checked((int)encodedLength);
            Assert.True(cursor <= payload.Length - length, "The WAL TLV is truncated.");
            var field = payload.Slice(cursor, length);
            cursor += length;
            switch (tag)
            {
                case 1:
                    Assert.Equal(1, length);
                    operation = field[0];
                    break;
                case 2:
                    Assert.Equal(sizeof(uint), length);
                    columnFamilyId = BinaryPrimitives.ReadUInt32LittleEndian(field);
                    break;
                case 3:
                    Assert.Equal(sizeof(ulong), length);
                    sequence = BinaryPrimitives.ReadUInt64LittleEndian(field);
                    break;
                case 4:
                    key = field.ToArray();
                    break;
                case 5:
                    value = field.ToArray();
                    break;
                case 6:
                    Assert.Equal(sizeof(ulong), length);
                    expiration = BinaryPrimitives.ReadUInt64LittleEndian(field);
                    break;
                case 7:
                    rangeEnd = field.ToArray();
                    break;
                case 8:
                    Assert.Equal(sizeof(ulong), length);
                    transactionId = BinaryPrimitives.ReadUInt64LittleEndian(field);
                    break;
                case 9:
                    Assert.Equal(1, length);
                    compression = field[0];
                    break;
                case 10:
                    Assert.Equal(sizeof(ulong), length);
                    writerEpoch = BinaryPrimitives.ReadUInt64LittleEndian(field);
                    break;
            }
        }

        Assert.Equal(payload.Length, cursor);
        Assert.True(operation.HasValue, "The WAL record does not contain an operation tag.");
        Assert.InRange(operation.Value, (byte)0, (byte)6);
        Assert.True(columnFamilyId.HasValue, "The WAL record does not contain a column-family tag.");
        Assert.True(sequence.HasValue, "The WAL record does not contain a sequence tag.");
        Assert.NotNull(key);
        Assert.True(writerEpoch.HasValue, "The WAL record does not contain a writer-epoch tag.");
        if (value is not null)
        {
            value = MidgeDiskFormat.Decompress(value, compression ?? 0);
        }

        switch (operation.Value)
        {
            case 0:
            case 1:
                Assert.NotNull(value);
                break;
            case 3:
                Assert.NotNull(rangeEnd);
                break;
            case 4:
            case 5:
                Assert.True(transactionId.HasValue, "A transaction marker must contain a transaction id.");
                break;
            case 6:
                Assert.NotNull(value);
                Assert.True(transactionId.HasValue, "A transaction batch must contain a transaction id.");
                break;
        }

        return new MidgeWalTestRecord(
            operation.Value,
            columnFamilyId.Value,
            sequence.Value,
            key,
            value,
            expiration,
            rangeEnd,
            transactionId,
            writerEpoch.Value,
            compression,
            payloadLength);
    }
}
