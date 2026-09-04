using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsRuntimeOperationsTests
{
    [Fact]
    public async Task ShouldFlushOnlyRequestedColumnFamily()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        var flushed = await database.ColumnFamilies.CreateAsync("flushed");
        var pending = await database.ColumnFamilies.CreateAsync("pending");
        await PutAsync(database, flushed, "flushed-key", "flushed-value");
        await PutAsync(database, pending, "pending-key", "pending-value");
        var bytesBefore = (await database.Diagnostics.GetRuntimeMetricsAsync()).TotalMemtableBytes;

        await database.Maintenance.FlushAsync(flushed);

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.InRange(metrics.TotalMemtableBytes, 1, bytesBefore - 1);
        await database.ColumnFamilies.DropAsync(flushed);
        var error = await Assert.ThrowsAsync<PantsBusyException>(() =>
            database.ColumnFamilies.DropAsync(pending).AsTask());
        Assert.Equal(PantsErrorCode.Busy, error.Code);
    }

    [Fact]
    public async Task ShouldAutoFlushLocalMemtableAtConfiguredThreshold()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(8 * 1024))
            .WithMemtableLimits(2 * 1024, 1024)
            .WithTransactionMemoryPool(2 * 1024);
        await using var database = await PantsDatabase.OpenAsync(options);
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("large"u8.ToArray(), new byte[1200]);
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        var scheduled = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(scheduled.FlushEnqueuedTotal >= 1);

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        var cleared = await database.Maintenance.WaitForWriteStallClearAsync(
            database.ColumnFamilies.DefaultFamily,
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
        var options = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(256 * 1024))
            .WithMemtableLimits(64 * 1024, 16 * 1024)
            .WithTransactionMemoryPool(64 * 1024)
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenAsync(options);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(index =>
            PutWithWriteStallRetryAsync(
                database,
                $"key-{index:000}",
                new byte[512]).AsTask()));

        Assert.True(await database.Maintenance.WaitForWriteStallClearAsync(
            database.ColumnFamilies.DefaultFamily,
            TimeSpan.FromSeconds(2)));
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.False(metrics.WriteStalled);
        Assert.True(metrics.SstCount >= 2);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
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
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(TestBytes.FromString($"memory-{index}"), new byte[200]);
            await transaction.CommitAsync(PantsWriteOptions.BestEffort);
        }

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.False(metrics.WriteStalled);
        Assert.Equal(PantsEngineHealth.Healthy, metrics.Health);

        await using var accepted = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        accepted.Put("accepted"u8.ToArray(), new byte[200]);
        await accepted.CommitAsync(PantsWriteOptions.BestEffort);

        Assert.True(await database.Maintenance.WaitForWriteStallClearAsync(
            database.ColumnFamilies.DefaultFamily,
            TimeSpan.Zero));
    }

    static async ValueTask PutWithWriteStallRetryAsync(
        IPantsDatabase database,
        string key,
        byte[] value)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put(TestBytes.FromString(key), value);
            try
            {
                await transaction.CommitAsync(PantsWriteOptions.Buffered);
                return;
            }
            catch (PantsWriteStallException) when (attempt < 15)
            {
                Assert.True(await database.Maintenance.WaitForWriteStallClearAsync(
                    database.ColumnFamilies.DefaultFamily,
                    TimeSpan.FromSeconds(2)));
            }
        }

        throw new InvalidOperationException("Write pressure did not clear within 16 attempts.");
    }

    static async ValueTask PutAsync(
        IPantsDatabase database,
        IPantsColumnFamily columnFamily,
        string key,
        string value)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            columnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Buffered);
    }

    [Fact]
    public async Task ShouldValidateWriteStallWaitArgumentsAndColumnFamilyHandle()
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        var family = await database.ColumnFamilies.CreateAsync("temporary");
        await database.ColumnFamilies.DropAsync(family);

        var stale = await Assert.ThrowsAnyAsync<PantsException>(() => database.Maintenance
            .WaitForWriteStallClearAsync(family, TimeSpan.Zero)
            .AsTask());
        var timeout = await Assert.ThrowsAnyAsync<PantsException>(() => database.Maintenance
            .WaitForWriteStallClearAsync(
                database.ColumnFamilies.DefaultFamily,
                TimeSpan.FromTicks(-1))
            .AsTask());

        Assert.Equal(PantsErrorCode.InvalidArgument, stale.Code);
        Assert.Equal(PantsErrorCode.InvalidArgument, timeout.Code);
    }
}
