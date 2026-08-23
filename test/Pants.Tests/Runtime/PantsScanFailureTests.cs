namespace Cntryl.Pants.Tests.Runtime;

public sealed class PantsScanFailureTests
{
    [Fact]
    public async Task ShouldAdvanceLazilyAndKeepSourceFailureSticky()
    {
        var expected = new InvalidDataException("terminal read failure");
        var releases = 0;
        await using var scan = new ScanInstance(
            _ => ValueTask.FromResult(ThrowAfterFirst(expected).GetEnumerator()),
            new PantsScanQuery(),
            () =>
            {
                releases++;
                return ValueTask.CompletedTask;
            });
        var enumerator = scan.GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("first", TestBytes.ToText(scan.Current.Key));
        var first = await Assert.ThrowsAsync<InvalidDataException>(() => enumerator.MoveNextAsync().AsTask());
        var second = await Assert.ThrowsAsync<InvalidDataException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Same(expected, first);
        Assert.Same(first, second);
        Assert.True(scan.IsFailed);
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task ShouldKeepTerminalFailureStickyGivenCorruptSst()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using (var writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("corrupt-key"u8.ToArray(), "corrupt-value"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.FlushAsync(database.DefaultColumnFamily);
        await CorruptFirstSstDataByteAsync(directory.Path);
        await using var reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var enumerator = scan.GetAsyncEnumerator();

        var first = await Assert.ThrowsAsync<StorageException>(() => enumerator.MoveNextAsync().AsTask());
        var second = await Assert.ThrowsAsync<StorageException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Same(first, second);
        Assert.True(scan.IsFailed);
        Assert.Equal(PantsIteratorState.Failed, scan.State);
        Assert.Equal(1, (await database.GetRuntimeMetricsAsync()).ActiveSnapshots);
    }

    static async Task CorruptFirstSstDataByteAsync(string databasePath)
    {
        var sstPath = Directory.EnumerateFiles(Path.Combine(databasePath, "sst"), "*.sst").First();
        await using var stream = new FileStream(
            sstPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            1,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        stream.Position = sizeof(uint);
        var value = stream.ReadByte();
        Assert.NotEqual(-1, value);
        stream.Position = sizeof(uint);
        stream.WriteByte(checked((byte)(value ^ 1)));
        await stream.FlushAsync();
        stream.Flush(true);
    }

    static IEnumerable<PantsEntry> ThrowAfterFirst(Exception exception)
    {
        yield return new PantsEntry("first"u8.ToArray(), "value"u8.ToArray());
        throw exception;
    }
}
