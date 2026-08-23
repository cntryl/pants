namespace Cntryl.Pants.Tests;

public sealed class PantsScanContractTests
{
    [Fact]
    public async Task ShouldScanPrefixIntersectionInBothDirectionsWithLimit()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        foreach (string key in new[] { "aa0", "aa1", "aa2", "ab0", "b00" })
        {
            transaction.Put(TestBytes.FromString(key), TestBytes.FromString($"v-{key}"));
        }

        await using IPantsScan scan = await transaction.ScanAsync(new PantsScanQuery
        {
            StartInclusive = TestBytes.FromString("aa1"),
            EndExclusive = TestBytes.FromString("ab9"),
            Prefix = TestBytes.FromString("aa"),
            Direction = PantsScanDirection.Reverse,
            Limit = 2
        });
        var keys = new List<string>();
        await foreach (PantsEntry entry in scan)
        {
            keys.Add(TestBytes.ToText(entry.Key));
        }

        Assert.Equal(["aa2", "aa1"], keys);
        Assert.True(scan.IsExhausted);
        Assert.False(scan.IsFailed);
    }

    [Fact]
    public async Task ShouldHonorPrefixEndingInFf()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(new byte[] { 0xff, 0x00 }, TestBytes.FromString("one"));
        transaction.Put(new byte[] { 0xff, 0xff }, TestBytes.FromString("two"));
        transaction.Put(new byte[] { 0xfe, 0xff }, TestBytes.FromString("other"));
        Assert.NotNull(await transaction.GetAsync(new byte[] { 0xff, 0x00 }));

        await using IPantsScan scan = await transaction.ScanAsync(new PantsScanQuery
        {
            Prefix = new byte[] { 0xff }
        });
        var entries = new List<PantsEntry>();
        await foreach (PantsEntry entry in scan)
        {
            entries.Add(entry);
        }

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public async Task ShouldMakeScanFailureStickyAfterCancellation()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString("key"), TestBytes.FromString("value"));
        await using IPantsScan scan = await transaction.ScanAsync(new PantsScanQuery());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        IAsyncEnumerator<PantsEntry> enumerator = scan.GetAsyncEnumerator(cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
        Assert.True(scan.IsFailed);
    }

    [Fact]
    public async Task ShouldReturnNoRowsForZeroLimit()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString("key"), TestBytes.FromString("value"));
        await using IPantsScan scan = await transaction.ScanAsync(new PantsScanQuery { Limit = 0 });

        Assert.Empty(await CollectAsync(scan));
        Assert.Equal(PantsIteratorState.Exhausted, scan.State);
    }

    [Fact]
    public async Task ShouldRejectReversedBoundsButAllowEqualBounds()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        PantsException error = await Assert.ThrowsAnyAsync<PantsException>(() => transaction.ScanAsync(
            new PantsScanQuery
            {
                StartInclusive = "z"u8.ToArray(),
                EndExclusive = "a"u8.ToArray()
            }).AsTask());
        await using IPantsScan equal = await transaction.ScanAsync(new PantsScanQuery
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
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
        await using IPantsScan scan = await transaction.ScanAsync(new PantsScanQuery());

        await transaction.RollbackAsync();

        PantsRuntimeMetrics pinned = await database.GetRuntimeMetricsAsync();
        Assert.Equal(1, pinned.ActiveSnapshots);
        PantsEntry entry = Assert.Single(await CollectAsync(scan));
        Assert.Equal("key", TestBytes.ToText(entry.Key));
        Assert.Equal("value", TestBytes.ToText(entry.Value));
        Assert.Equal(0, (await database.GetRuntimeMetricsAsync()).ActiveSnapshots);
    }

    private static async Task<IReadOnlyList<PantsEntry>> CollectAsync(IPantsScan scan)
    {
        var entries = new List<PantsEntry>();
        await foreach (PantsEntry entry in scan)
        {
            entries.Add(entry);
        }

        return entries;
    }
}
