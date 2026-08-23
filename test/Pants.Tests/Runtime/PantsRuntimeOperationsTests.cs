namespace Cntryl.Pants.Tests;

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

        var metrics = await database.GetRuntimeMetricsAsync();
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

        var scheduled = await database.GetRuntimeMetricsAsync();
        Assert.True(scheduled.FlushEnqueuedTotal >= 1);

        await database.FlushAsync(database.DefaultColumnFamily);
        var metrics = await database.GetRuntimeMetricsAsync();
        bool cleared = await database.WaitForWriteStallClearAsync(
            database.DefaultColumnFamily,
            TimeSpan.Zero);

        Assert.True(cleared);
        Assert.False(metrics.WriteStalled);
        Assert.Equal(0, metrics.TotalMemtableBytes);
        Assert.Equal(1, metrics.ActiveMemtables);
        Assert.Equal(1, metrics.SstCount);
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

        await Task.WhenAll(Enumerable.Range(0, 200).Select(index =>
            PutWithWriteStallRetryAsync(
                database,
                $"key-{index:000}",
                new byte[512]).AsTask()));

        Assert.True(await database.WaitForWriteStallClearAsync(
            database.DefaultColumnFamily,
            TimeSpan.FromSeconds(2)));
        PantsRuntimeMetrics metrics = await database.GetRuntimeMetricsAsync();
        Assert.False(metrics.WriteStalled);
        Assert.True(metrics.SstCount >= 2);
        await using IPantsTransaction reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.NotNull(await reader.GetAsync(TestBytes.FromString("key-000")));
        Assert.NotNull(await reader.GetAsync(TestBytes.FromString("key-199")));
    }

    [Fact]
    public async Task ShouldKeepInMemoryWritesAvailableAfterDurableMemtableLimitIsReached()
    {
        var options = PantsOpenOptions.InMemory()
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(2 * 1024))
            .WithMemtableLimits(512, 256)
            .WithTransactionMemoryPool(512);
        await using var database = await PantsDatabase.OpenAsync(options);
        for (var index = 0; index < 2; index++)
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(TestBytes.FromString($"memory-{index}"), new byte[200]);
            await transaction.CommitAsync(PantsWriteOptions.BestEffort);
        }

        var metrics = await database.GetRuntimeMetricsAsync();
        Assert.False(metrics.WriteStalled);
        Assert.Equal(PantsEngineHealth.Healthy, metrics.Health);

        await using var accepted = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        accepted.Put("accepted"u8.ToArray(), new byte[200]);
        await accepted.CommitAsync(PantsWriteOptions.BestEffort);

        Assert.True(await database.WaitForWriteStallClearAsync(
            database.DefaultColumnFamily,
            TimeSpan.Zero));
    }

    static async ValueTask PutWithWriteStallRetryAsync(
        IPantsDatabase database,
        string key,
        byte[] value)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(TestBytes.FromString(key), value);
            try
            {
                await transaction.CommitAsync(PantsWriteOptions.Buffered);
                return;
            }
            catch (PantsWriteStallException) when (attempt < 15)
            {
                Assert.True(await database.WaitForWriteStallClearAsync(
                    database.DefaultColumnFamily,
                    TimeSpan.FromSeconds(2)));
            }
        }

        throw new InvalidOperationException("Write pressure did not clear within 16 attempts.");
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
