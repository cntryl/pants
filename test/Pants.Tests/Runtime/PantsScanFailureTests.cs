using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsScanFailureTests
{
    [Fact]
    public async Task ShouldRemainExhaustedAndReleaseSnapshotOnceWhenMovedAfterDisposal()
    {
        var releases = 0;
        var scan = new ScanInstance(
            _ => ValueTask.FromResult(
                new[] { new PantsEntry("key"u8.ToArray(), "value"u8.ToArray()) }
                    .AsEnumerable().GetEnumerator()),
            new PantsScanQuery(),
            () =>
            {
                releases++;
                return ValueTask.CompletedTask;
            });
        var enumerator = scan.GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());

        await scan.DisposeAsync();
        var moved = await enumerator.MoveNextAsync();
        await scan.DisposeAsync();

        Assert.False(moved);
        Assert.Equal(PantsIteratorState.Exhausted, scan.State);
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task ShouldKeepBusyFailureStickyWhenEnumeratedTwice()
    {
        var releases = 0;
        await using var scan = new ScanInstance(
            _ => ValueTask.FromResult(
                Array.Empty<PantsEntry>().AsEnumerable().GetEnumerator()),
            new PantsScanQuery(),
            () =>
            {
                releases++;
                return ValueTask.CompletedTask;
            });
        var original = scan.GetAsyncEnumerator();

        var failure = Assert.Throws<PantsBusyException>(() => scan.GetAsyncEnumerator());
        var originalFailure = await Assert.ThrowsAsync<PantsBusyException>(() =>
            original.MoveNextAsync().AsTask());

        Assert.Same(failure, originalFailure);
        Assert.Equal(PantsErrorCode.Busy, failure.Code);
        Assert.Equal(PantsIteratorState.Failed, scan.State);
        Assert.Equal(0, releases);
    }

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
        await using (var writer = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("corrupt-key"u8.ToArray(), "corrupt-value"u8.ToArray());
            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        await CorruptFirstSstDataByteAsync(directory.Path);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var enumerator = scan.GetAsyncEnumerator();

        var first = await Assert.ThrowsAsync<StorageException>(() => enumerator.MoveNextAsync().AsTask());
        var second = await Assert.ThrowsAsync<StorageException>(() => enumerator.MoveNextAsync().AsTask());

        Assert.Same(first, second);
        Assert.True(scan.IsFailed);
        Assert.Equal(PantsIteratorState.Failed, scan.State);
        Assert.Equal(1, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
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
