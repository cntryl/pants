using System.Buffers.Binary;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Transactions.Spill;

public sealed class PantsTransactionSpillCorruptionScanTests
{
    const int RunHeaderLength = 48;
    const int OrdinalTableOffsetPosition = 20;
    const long ForwardPoolBytes = 1_024;
    const long ReversePoolBytes = 4_096;
    const int ReverseRecordCount = 40;
    const int ForwardRecordCount = 6;

    [Fact]
    public async Task ShouldSurfaceLateSpillRunCorruptionThroughForwardScanGivenCorruptFrameAfterFirstKey()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillHardeningTestHarness.OpenLocalAsync(
            directory.Path,
            ForwardPoolBytes);
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        PutSmallRows(transaction, "row", ForwardRecordCount);
        transaction.Put("zz:spill-trigger"u8.ToArray(), Enumerable.Repeat((byte)'z', 900).ToArray());
        var run = Assert.Single(FindRunFiles(directory.Path));
        Assert.Equal(ForwardRecordCount, CountDataFrames(run));

        await using var scan = await transaction.ScanAsync(new PantsScanQuery
        {
            Prefix = "row:"u8.ToArray()
        });
        await using var enumerator = scan.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("row:000", TestBytes.ToText(enumerator.Current.Key));

        // Frame index 3 is row:003. Rows 000 and 001 remain readable without touching it.
        CorruptDataFrame(run, 3);

        var yielded = new List<string> { "row:000" };
        var error = await Assert.ThrowsAsync<PantsCorruptionException>(async () =>
        {
            while (await enumerator.MoveNextAsync())
            {
                yielded.Add(TestBytes.ToText(enumerator.Current.Key));
            }
        });

        Assert.Equal(PantsErrorCode.Corruption, error.Code);
        Assert.Equal(["row:000", "row:001"], yielded);
        Assert.True(scan.IsFailed);
        Assert.False(scan.IsExhausted);

        var repeated = await Assert.ThrowsAsync<PantsCorruptionException>(() => enumerator.MoveNextAsync().AsTask());
        Assert.Same(error, repeated);
        Assert.True(scan.IsFailed);

        var pointError = await Assert.ThrowsAsync<PantsCorruptionException>(() =>
            transaction.GetAsync("row:003"u8.ToArray()).AsTask());
        Assert.Equal(PantsErrorCode.Corruption, pointError.Code);
    }

    [Fact]
    public async Task ShouldSurfaceLateSpillRunCorruptionThroughReverseScanGivenCorruptEarlierSparseChunk()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillHardeningTestHarness.OpenLocalAsync(
            directory.Path,
            ReversePoolBytes);
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        PutSmallRows(transaction, "row", ReverseRecordCount);
        transaction.Put("zz:spill-trigger"u8.ToArray(), Enumerable.Repeat((byte)'z', 2_000).ToArray());
        var run = Assert.Single(FindRunFiles(directory.Path));
        Assert.Equal(ReverseRecordCount, CountDataFrames(run));

        await using var scan = await transaction.ScanAsync(new PantsScanQuery
        {
            Prefix = "row:"u8.ToArray(),
            Direction = PantsScanDirection.Reverse
        });
        await using var enumerator = scan.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("row:039", TestBytes.ToText(enumerator.Current.Key));

        // Sparse chunk 0 covers frames 0-15. The reverse scan has only loaded chunk 2 so far.
        CorruptDataFrame(run, 5);

        var yielded = new List<string> { "row:039" };
        var error = await Assert.ThrowsAsync<PantsCorruptionException>(async () =>
        {
            while (await enumerator.MoveNextAsync())
            {
                yielded.Add(TestBytes.ToText(enumerator.Current.Key));
            }
        });

        Assert.Equal(PantsErrorCode.Corruption, error.Code);
        Assert.Equal(
            Enumerable.Range(17, 23).Reverse().Select(static index => $"row:{index:000}").ToArray(),
            yielded.ToArray());
        Assert.True(scan.IsFailed);

        var repeated = await Assert.ThrowsAsync<PantsCorruptionException>(() => enumerator.MoveNextAsync().AsTask());
        Assert.Same(error, repeated);
        Assert.True(scan.IsFailed);
    }

    [Fact]
    public async Task ShouldReleaseSpillRunsAndSnapshotPinsGivenCorruptedScanIsDisposedAndRolledBack()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await TransactionSpillHardeningTestHarness.OpenLocalAsync(
            directory.Path,
            ForwardPoolBytes);
        var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        PutSmallRows(transaction, "row", ForwardRecordCount);
        transaction.Put("zz:spill-trigger"u8.ToArray(), Enumerable.Repeat((byte)'z', 900).ToArray());
        var run = Assert.Single(FindRunFiles(directory.Path));

        var scan = await transaction.ScanAsync(new PantsScanQuery
        {
            Prefix = "row:"u8.ToArray()
        });
        var enumerator = scan.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        CorruptDataFrame(run, 3);
        await Assert.ThrowsAsync<PantsCorruptionException>(async () =>
        {
            while (await enumerator.MoveNextAsync())
            {
            }
        });
        Assert.True(scan.IsFailed);

        await scan.DisposeAsync();
        await transaction.RollbackAsync();
        await transaction.DisposeAsync();

        Assert.Empty(FindArtifactFiles(directory.Path));
        Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
    }

    static void PutSmallRows(IPantsTransaction transaction, string prefix, int count)
    {
        for (var index = 0; index < count; index++)
        {
            transaction.Put(TestBytes.FromString($"{prefix}:{index:000}"), "v"u8.ToArray());
        }
    }

    static string[] FindRunFiles(string databasePath) =>
        FindArtifactFiles(databasePath)
            .Where(static path => path.EndsWith(".run", StringComparison.Ordinal))
            .ToArray();

    static string[] FindArtifactFiles(string databasePath) =>
        TransactionSpillHardeningTestHarness.FindArtifacts(databasePath);

    static int CountDataFrames(string runPath)
    {
        using var stream = new FileStream(runPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var ordinalTableOffset = ReadHeaderOrdinalTableOffset(stream);
        var count = 0;
        var offset = (long)RunHeaderLength;
        while (offset < checked((long)ordinalTableOffset))
        {
            offset += FrameLength(stream, offset);
            count++;
        }

        return count;
    }

    static void CorruptDataFrame(string runPath, int frameIndex)
    {
        using var stream = new FileStream(
            runPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        var ordinalTableOffset = checked((long)ReadHeaderOrdinalTableOffset(stream));
        var offset = (long)RunHeaderLength;
        for (var index = 0; offset < ordinalTableOffset; index++)
        {
            var payloadLength = checked((long)ReadPayloadLength(stream, offset));
            if (index == frameIndex)
            {
                var lastPayloadByte = offset + 8 + payloadLength - 1;
                stream.Position = lastPayloadByte;
                var original = stream.ReadByte();
                Assert.NotEqual(-1, original);
                stream.Position = lastPayloadByte;
                stream.WriteByte((byte)(original ^ 0xFF));
                stream.Flush(true);
                return;
            }

            offset += 8 + payloadLength;
        }

        Assert.Fail($"The spill run has no data frame at index {frameIndex}.");
    }

    static ulong ReadHeaderOrdinalTableOffset(FileStream stream)
    {
        Span<byte> header = stackalloc byte[RunHeaderLength];
        stream.Position = 0;
        stream.ReadExactly(header);
        return BinaryPrimitives.ReadUInt64LittleEndian(header[OrdinalTableOffsetPosition..]);
    }

    static long FrameLength(FileStream stream, long offset) =>
        8 + checked((long)ReadPayloadLength(stream, offset));

    static uint ReadPayloadLength(FileStream stream, long offset)
    {
        Span<byte> frameHeader = stackalloc byte[8];
        stream.Position = offset;
        stream.ReadExactly(frameHeader);
        return BinaryPrimitives.ReadUInt32LittleEndian(frameHeader);
    }
}
