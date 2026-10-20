using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsStreamingScanHardeningTests
{
    [Theory]
    [InlineData(PantsScanDirection.Forward)]
    [InlineData(PantsScanDirection.Reverse)]
    public async Task ShouldReadOnlyOneCandidateBlockGivenLimitOne(
        PantsScanDirection direction)
    {
        using var directory = new TemporaryDirectory();
        await using var database = await CreateMultiBlockDatabaseAsync(directory.Path);
        var before = await database.Diagnostics.GetReadPathDiagnosticsAsync();
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await transaction.ScanAsync(new PantsScanQuery
        {
            Direction = direction,
            Limit = 1
        });

        var entry = Assert.Single(await CollectAsync(scan));
        Assert.Equal(
            direction == PantsScanDirection.Forward ? "key-0000" : "key-0127",
            TestBytes.ToText(entry.Key));
        var after = await database.Diagnostics.GetReadPathDiagnosticsAsync();
        Assert.Equal(before.DataBlocksRead + 1, after.DataBlocksRead);
    }

    [Fact]
    public async Task ShouldShareSstFanInBudgetAcrossScansAndReleaseItOnDisposal()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(20 * 1024))
            .WithMemtableLimits(512)
            .WithCompaction(new PantsCompactionConfiguration(
                L0FileCountTrigger: 1000,
                BackgroundEnabled: false));
        Assert.Equal(1024, PantsOpenOptionsResolver.Resolve(options).ScanMemoryPoolBytes);

        await using var database = await PantsDatabase.OpenAsync(options);
        for (var index = 0; index < 20; index++)
        {
            await using var writer = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            writer.Put(TestBytes.FromString($"key-{index:0000}"), new byte[] { 1 });
            writer.DeleteRange(TestBytes.FromString("z-start"), TestBytes.FromString("zz-end"));
            await writer.CommitAsync(PantsWriteOptions.Buffered);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        var metricsBeforeScan = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(20, metricsBeforeScan.SstCount);
        await using var firstTransaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await using var firstScan = await firstTransaction.ScanAsync(new PantsScanQuery());
        var firstEnumerator = firstScan.GetAsyncEnumerator();
        Assert.True(await firstEnumerator.MoveNextAsync());
        Assert.True(firstScan.IsActive);
        var metricsDuringFirstScan = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(
            metricsDuringFirstScan.ScanBufferUsedBytes > 0,
            $"Expected scan memory to be reserved, found {metricsDuringFirstScan.ScanBufferUsedBytes} bytes.");

        await using var secondTransaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await using var secondScan = await secondTransaction.ScanAsync(new PantsScanQuery());
        var secondEnumerator = secondScan.GetAsyncEnumerator();

        var failure = await Assert.ThrowsAsync<PantsResourceLimitException>(
            () => secondEnumerator.MoveNextAsync().AsTask());

        Assert.Equal(PantsErrorCode.ResourceLimit, failure.Code);
        Assert.True(secondScan.IsFailed);
        Assert.True(firstScan.IsActive);
        Assert.Equal(
            metricsDuringFirstScan.ScanBufferUsedBytes,
            (await database.Diagnostics.GetRuntimeMetricsAsync()).ScanBufferUsedBytes);

        await firstScan.DisposeAsync();
        var metricsAfterDisposal = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(
            metricsAfterDisposal.ScanBufferUsedBytes == 0,
            $"Scan memory remained reserved after disposal: {metricsAfterDisposal.ScanBufferUsedBytes} bytes.");
    }

    [Fact]
    public async Task ShouldYieldEarlierRowsBeforeAStickyLaterBlockFailure()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await CreateMultiBlockDatabaseAsync(directory.Path);
        CorruptDataBlock(directory.Path, 1);
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await transaction.ScanAsync(new PantsScanQuery());
        var enumerator = scan.GetAsyncEnumerator();
        var emitted = 0;
        PantsCorruptionException? first = null;
        while (first is null)
        {
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }

                emitted++;
            }
            catch (PantsCorruptionException exception)
            {
                first = exception;
            }
        }

        Assert.True(emitted > 0);
        Assert.NotNull(first);
        var second = await Assert.ThrowsAsync<PantsCorruptionException>(() => enumerator.MoveNextAsync().AsTask());
        Assert.Same(first, second);
        Assert.True(scan.IsFailed);
        Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).ScanBufferUsedBytes);
    }

    [Fact]
    public async Task ShouldKeepIndependentSstHandlesStableAcrossCompactionAndFlush()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await CreateMultiBlockDatabaseAsync(directory.Path);
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await transaction.ScanAsync(new PantsScanQuery());
        var enumerator = scan.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());

        await database.Maintenance.CompactAllAsync();
        await using (var writer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put(TestBytes.FromString("key-new"), TestBytes.FromString("new"));
            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var count = 1;
        while (await enumerator.MoveNextAsync())
        {
            count++;
            Assert.NotEqual("key-new", TestBytes.ToText(enumerator.Current.Key));
        }

        Assert.Equal(128, count);
    }

    static async ValueTask<IPantsDatabase> CreateMultiBlockDatabaseAsync(string path)
    {
        var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(path).WithBackgroundCompaction(false));
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        for (var index = 0; index < 128; index++)
        {
            transaction.Put(
                TestBytes.FromString($"key-{index:0000}"),
                new byte[1024]);
        }

        await transaction.CommitAsync(PantsWriteOptions.Buffered);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        return database;
    }

    static async Task<IReadOnlyList<PantsEntry>> CollectAsync(IPantsScan scan)
    {
        var entries = new List<PantsEntry>();
        await foreach (var entry in scan)
        {
            entries.Add(entry);
        }

        return entries;
    }

    static void CorruptDataBlock(string path, int blockIndex)
    {
        var sstPath = Assert.Single(Directory.GetFiles(Path.Combine(path, "sst"), "*.sst"));
        SstBlockHandle handle;
        using (var reader = SstReader.Open(sstPath))
        {
            Assert.True(reader.DataBlockCount > blockIndex);
            handle = reader.GetDataBlockHandle(blockIndex);
        }

        using var stream = new FileStream(sstPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        stream.Position = checked((long)handle.Offset + sizeof(uint));
        var value = stream.ReadByte();
        Assert.NotEqual(-1, value);
        stream.Position = checked((long)handle.Offset + sizeof(uint));
        stream.WriteByte(checked((byte)(value ^ 1)));
        stream.Flush(true);
    }
}
