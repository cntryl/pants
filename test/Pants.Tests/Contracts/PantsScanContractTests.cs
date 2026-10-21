using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Contracts;

public sealed class PantsScanContractTests
{
    [Fact]
    public async Task ShouldScanPrefixIntersectionInBothDirectionsWithLimit()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        foreach (var key in new[] { "aa0", "aa1", "aa2", "ab0", "b00" })
        {
            transaction.Put(TestBytes.FromString(key), TestBytes.FromString($"v-{key}"));
        }

        await using var scan = await transaction.ScanAsync(new PantsScanQuery
        {
            StartInclusive = TestBytes.FromString("aa1"),
            EndExclusive = TestBytes.FromString("ab9"),
            Prefix = TestBytes.FromString("aa"),
            Direction = PantsScanDirection.Reverse,
            Limit = 2
        });
        var keys = new List<string>();
        await foreach (var entry in scan)
        {
            keys.Add(TestBytes.ToText(entry.Key));
        }

        Assert.Equal(["aa2", "aa1"], keys);
        Assert.True(scan.IsExhausted);
        Assert.False(scan.IsFailed);
    }

    [Fact]
    public async Task ShouldStopPrefixScanBeforeNeighboringPrefix()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await WriteRowsAsync(database,
            ("aa:1"u8.ToArray(), "one"u8.ToArray()),
            ("aa:2"u8.ToArray(), "two"u8.ToArray()),
            ("ab:0"u8.ToArray(), "other"u8.ToArray()));

        var rows = await ScanAsync(database, new PantsScanQuery { Prefix = "aa:"u8.ToArray() });

        Assert.Equal(["aa:1", "aa:2"], rows.Select(static row => TestBytes.ToText(row.Key)).ToArray());
    }

    [Fact]
    public async Task ShouldIntersectPrefixWithExplicitEndBound()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await WriteRowsAsync(database,
            ("scan:1"u8.ToArray(), "one"u8.ToArray()),
            ("scan:2"u8.ToArray(), "two"u8.ToArray()),
            ("scan:3"u8.ToArray(), "three"u8.ToArray()),
            ("scao:0"u8.ToArray(), "neighbor"u8.ToArray()));

        var rows = await ScanAsync(database, new PantsScanQuery
        {
            Prefix = "scan:"u8.ToArray(),
            EndExclusive = "scan:2"u8.ToArray()
        });

        Assert.Equal(["scan:1"], rows.Select(static row => TestBytes.ToText(row.Key)).ToArray());
    }

    [Fact]
    public async Task ShouldReturnEmptyGivenValidBoundsDisjointFromPrefix()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await WriteRowsAsync(database,
            ("alpha:1"u8.ToArray(), "one"u8.ToArray()),
            ("zulu:1"u8.ToArray(), "two"u8.ToArray()));

        var rows = await ScanAsync(database, new PantsScanQuery
        {
            StartInclusive = "zulu"u8.ToArray(),
            EndExclusive = "zulu;"u8.ToArray(),
            Prefix = "alpha:"u8.ToArray()
        });

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ShouldScanBinaryPrefixWithoutFiniteSuccessor()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await WriteRowsAsync(database,
            (new byte[] { 0xfe, 0xff }, "other"u8.ToArray()),
            (new byte[] { 0xff }, "value"u8.ToArray()),
            (new byte[] { 0xff, 0x00 }, "value"u8.ToArray()),
            (new byte[] { 0xff, 0xff, 0x01 }, "value"u8.ToArray()));

        var rows = await ScanAsync(database, new PantsScanQuery { Prefix = new byte[] { 0xff } });

        Assert.Collection(
            rows,
            row => Assert.Equal(new byte[] { 0xff }, row.Key.ToArray()),
            row => Assert.Equal(new byte[] { 0xff, 0x00 }, row.Key.ToArray()),
            row => Assert.Equal(new byte[] { 0xff, 0xff, 0x01 }, row.Key.ToArray()));
        Assert.All(rows, static row => Assert.Equal("value", TestBytes.ToText(row.Value)));
    }

    [Fact]
    public async Task ShouldHonorPrefixEndingInFf()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(new byte[] { 0xff, 0x00 }, TestBytes.FromString("one"));
        transaction.Put(new byte[] { 0xff, 0xff }, TestBytes.FromString("two"));
        transaction.Put(new byte[] { 0xfe, 0xff }, TestBytes.FromString("other"));
        Assert.NotNull(await transaction.GetAsync(new byte[] { 0xff, 0x00 }));

        await using var scan = await transaction.ScanAsync(new PantsScanQuery
        {
            Prefix = new byte[] { 0xff }
        });
        var entries = new List<PantsEntry>();
        await foreach (var entry in scan)
        {
            entries.Add(entry);
        }

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task ShouldMakeScanFailureStickyAfterCancellation()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString("key"), TestBytes.FromString("value"));
        await using var scan = await transaction.ScanAsync(new PantsScanQuery());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var enumerator = scan.GetAsyncEnumerator(cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
        Assert.True(scan.IsFailed);
    }

    [Fact]
    public async Task ShouldReturnNoRowsForZeroLimit()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString("key"), TestBytes.FromString("value"));
        await using var scan = await transaction.ScanAsync(new PantsScanQuery { Limit = 0 });

        Assert.Empty(await CollectAsync(scan));
        Assert.Equal(PantsIteratorState.Exhausted, scan.State);
    }

    [Fact]
    public async Task ShouldRejectReversedBoundsButAllowEqualBounds()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        var error = await Assert.ThrowsAnyAsync<PantsException>(() => transaction.ScanAsync(
            new PantsScanQuery
            {
                StartInclusive = "z"u8.ToArray(),
                EndExclusive = "a"u8.ToArray()
            }).AsTask());
        await using var equal = await transaction.ScanAsync(new PantsScanQuery
        {
            StartInclusive = "a"u8.ToArray(),
            EndExclusive = "a"u8.ToArray()
        });

        Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
        Assert.Empty(await CollectAsync(equal));
    }

    [Fact]
    public async Task ScanOwnsSnapshotPinAfterTransactionCompletes()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
        await using var scan = await transaction.ScanAsync(new PantsScanQuery());

        await transaction.RollbackAsync();

        var pinned = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(1, pinned.ActiveSnapshots);
        var entry = Assert.Single(await CollectAsync(scan));
        Assert.Equal("key", TestBytes.ToText(entry.Key));
        Assert.Equal("value", TestBytes.ToText(entry.Value));
        Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).ActiveSnapshots);
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

    static async Task WriteRowsAsync(
        IPantsDatabase database,
        params (byte[] Key, byte[] Value)[] rows)
    {
        await using var writer = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        foreach (var row in rows)
        {
            writer.Put(row.Key, row.Value);
        }

        await writer.CommitAsync(PantsWriteOptions.Buffered);
    }

    static async Task<IReadOnlyList<PantsEntry>> ScanAsync(IPantsDatabase database, PantsScanQuery query)
    {
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(query);
        return await CollectAsync(scan);
    }
}
