namespace Pants.Tests;

public sealed class PantsRuntimeOperationsTests
{
    [Fact]
    public async Task ShouldFlushOnlyRequestedColumnFamily()
    {
        using var directory = new TemporaryDirectory();
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        IPantsColumnFamily flushed = await database.CreateColumnFamilyAsync("flushed");
        IPantsColumnFamily pending = await database.CreateColumnFamilyAsync("pending");
        await PutAsync(database, flushed, "flushed-key", "flushed-value");
        await PutAsync(database, pending, "pending-key", "pending-value");
        long bytesBefore = (await database.GetRuntimeMetricsAsync()).TotalMemtableBytes;

        await database.FlushAsync(flushed);

        PantsRuntimeMetrics metrics = await database.GetRuntimeMetricsAsync();
        Assert.InRange(metrics.TotalMemtableBytes, 1, bytesBefore - 1);
        await database.DropColumnFamilyAsync(flushed);
        PantsBusyException error = await Assert.ThrowsAsync<PantsBusyException>(() =>
            database.DropColumnFamilyAsync(pending).AsTask());
        Assert.Equal(PantsErrorCode.Busy, error.Code);
    }

    [Fact]
    public async Task ShouldAutoFlushLocalMemtableAtConfiguredThreshold()
    {
        using var directory = new TemporaryDirectory();
        PantsOpenOptions options = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(8 * 1024))
            .WithMemtableLimits(2 * 1024, 1024)
            .WithTransactionMemoryPool(2 * 1024);
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(options);
        await using (IPantsTransaction transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("large"u8.ToArray(), new byte[1200]);
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        PantsRuntimeMetrics metrics = await database.GetRuntimeMetricsAsync();
        bool cleared = await database.WaitForWriteStallClearAsync(
            database.DefaultColumnFamily,
            TimeSpan.Zero);

        Assert.True(cleared);
        Assert.False(metrics.WriteStalled);
        Assert.Equal(0, metrics.TotalMemtableBytes);
        Assert.Equal(1, metrics.ActiveMemtables);
        Assert.Equal(1, metrics.SstCount);
        Assert.True(metrics.FlushEnqueuedTotal >= 1);
        Assert.Equal(0, metrics.FlushFailuresTotal);
    }

    [Fact]
    public async Task ShouldRelieveConcurrentLocalWritePressureWithoutAStickyStall()
    {
        using var directory = new TemporaryDirectory();
        PantsOpenOptions options = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(256 * 1024))
            .WithMemtableLimits(64 * 1024, 16 * 1024)
            .WithTransactionMemoryPool(64 * 1024)
            .WithBackgroundCompaction(false);
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(options);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(async index =>
        {
            await using IPantsTransaction transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(TestBytes.FromString($"key-{index:000}"), new byte[512]);
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }));

        Assert.True(await database.WaitForWriteStallClearAsync(
            database.DefaultColumnFamily,
            TimeSpan.FromMilliseconds(500)));
        PantsRuntimeMetrics metrics = await database.GetRuntimeMetricsAsync();
        Assert.False(metrics.WriteStalled);
        Assert.True(metrics.SstCount >= 2);
        await using IPantsTransaction reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.NotNull(await reader.GetAsync(TestBytes.FromString("key-000")));
        Assert.NotNull(await reader.GetAsync(TestBytes.FromString("key-199")));
    }

    private static async ValueTask PutAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        string key,
        string value)
    {
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            columnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
    }

    [Fact]
    public async Task ShouldValidateWriteStallWaitArgumentsAndColumnFamilyHandle()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        IPantsColumnFamily family = await database.CreateColumnFamilyAsync("temporary");
        await database.DropColumnFamilyAsync(family);

        PantsException stale = await Assert.ThrowsAnyAsync<PantsException>(() => database
            .WaitForWriteStallClearAsync(family, TimeSpan.Zero)
            .AsTask());
        PantsException timeout = await Assert.ThrowsAnyAsync<PantsException>(() => database
            .WaitForWriteStallClearAsync(
                database.DefaultColumnFamily,
                TimeSpan.FromTicks(-1))
            .AsTask());

        Assert.Equal(PantsErrorCode.InvalidArgument, stale.Code);
        Assert.Equal(PantsErrorCode.InvalidArgument, timeout.Code);
    }
}
