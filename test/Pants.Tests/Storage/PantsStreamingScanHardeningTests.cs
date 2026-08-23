namespace Cntryl.Pants.Tests.Storage;

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
        var before = await database.GetReadPathDiagnosticsAsync();
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
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
        var after = await database.GetReadPathDiagnosticsAsync();
        Assert.Equal(before.DataBlocksRead + 1, after.DataBlocksRead);
    }

    [Fact]
    public async Task ShouldYieldEarlierRowsBeforeAStickyLaterBlockFailure()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await CreateMultiBlockDatabaseAsync(directory.Path);
        CorruptDataBlock(directory.Path, 1);
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await transaction.ScanAsync(new PantsScanQuery());
        var enumerator = scan.GetAsyncEnumerator();
        var emitted = 0;
        PantsStorageException? first = null;
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
            catch (PantsStorageException exception)
            {
                first = exception;
            }
        }

        Assert.True(emitted > 0);
        Assert.NotNull(first);
        var second = await Assert.ThrowsAsync<PantsStorageException>(() => enumerator.MoveNextAsync().AsTask());
        Assert.Same(first, second);
        Assert.True(scan.IsFailed);
    }

    [Fact]
    public async Task ShouldKeepIndependentSstHandlesStableAcrossCompactionAndFlush()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await CreateMultiBlockDatabaseAsync(directory.Path);
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await transaction.ScanAsync(new PantsScanQuery());
        var enumerator = scan.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());

        await database.CompactAllAsync();
        await using (var writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put(TestBytes.FromString("key-new"), TestBytes.FromString("new"));
            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
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
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        for (var index = 0; index < 128; index++)
        {
            transaction.Put(
                TestBytes.FromString($"key-{index:0000}"),
                new byte[1024]);
        }

        await transaction.CommitAsync(PantsWriteOptions.Buffered);
        await database.FlushAsync(database.DefaultColumnFamily);
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
        MidgeSstBlockHandle handle;
        using (var reader = MidgeSstReader.Open(sstPath))
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
