namespace Pants.Tests;

public sealed class PantsRuntimeOperationsTests
{
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
