using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsMemoryDropPressureTests
{
    [Fact]
    public async Task ShouldKeepGlobalStallUntilTheFamilyHoldingCommittedMemoryIsDiscarded()
    {
        await using var database = await OpenAsync();
        var observer = await database.ColumnFamilies.CreateAsync("observer");
        var pressure = await database.ColumnFamilies.CreateAsync("pressure");
        var empty = await database.ColumnFamilies.CreateAsync("empty");
        await WriteAsync(database, pressure, "pressure", new byte[256 * 1024]);
        Assert.False(await database.Maintenance.WaitForWriteStallClearAsync(observer, TimeSpan.Zero));

        await database.ColumnFamilies.DropAsync(empty);

        Assert.False(await database.Maintenance.WaitForWriteStallClearAsync(observer, TimeSpan.Zero));
        Assert.NotNull(await database.ColumnFamilies.GetAsync("pressure"));
        await database.ColumnFamilies.DropDiscardingUnflushedAsync(pressure);
        Assert.True(await database.Maintenance.WaitForWriteStallClearAsync(observer, TimeSpan.Zero));
        Assert.Null(await database.ColumnFamilies.GetAsync("pressure"));
    }

    [Fact]
    public async Task ShouldWakeAnAlreadyParkedMemoryWaiterAfterDiscardingTheDebtor()
    {
        await using var database = await OpenAsync();
        var observer = await database.ColumnFamilies.CreateAsync("observer");
        var pressure = await database.ColumnFamilies.CreateAsync("pressure");
        await WriteAsync(database, pressure, "pressure", new byte[256 * 1024]);
        Assert.False(await database.Maintenance.WaitForWriteStallClearAsync(observer, TimeSpan.Zero));
        var waiter = database.Maintenance.WaitForWriteStallClearAsync(observer, TestTimeouts.Expected).AsTask();
        // This command follows the wait-status capture on the same coordinator.
        Assert.NotNull(await database.ColumnFamilies.GetAsync("observer"));
        Assert.False(waiter.IsCompleted);

        await database.ColumnFamilies.DropDiscardingUnflushedAsync(pressure);

        Assert.True(await waiter.WaitAsync(TestTimeouts.Expected));
        await WriteAsync(database, observer, "accepted", [7]);
        await using var read = await database.Transactions.BeginAsync(observer, PantsTransactionMode.ReadOnly);
        Assert.Equal(new byte[] { 7 }, (await read.GetAsync("accepted"u8.ToArray()))?.ToArray());
    }

    [Fact]
    public async Task ShouldRefuseAnotherMemoryWriteBeforeAllocatingSequenceUntilPressureClears()
    {
        await using var database = await OpenAsync();
        var observer = await database.ColumnFamilies.CreateAsync("observer");
        var pressure = await database.ColumnFamilies.CreateAsync("pressure");
        await WriteAsync(database, pressure, "pressure", new byte[256 * 1024]);
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();
        await using (var rejected = await database.Transactions.BeginAsync(observer, PantsTransactionMode.ReadWrite))
        {
            rejected.Put("rejected"u8.ToArray(), new byte[] { 9 });

            await Assert.ThrowsAsync<PantsWriteStallException>(() => rejected.CommitAsync(PantsWriteOptions.Buffered).AsTask());
        }

        Assert.Equal(before.CurrentSequence, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
        await database.ColumnFamilies.DropDiscardingUnflushedAsync(pressure);
        await WriteAsync(database, observer, "accepted", [7]);
        await using var read = await database.Transactions.BeginAsync(observer, PantsTransactionMode.ReadOnly);
        Assert.Null(await read.GetAsync("rejected"u8.ToArray()));
        Assert.Equal(new byte[] { 7 }, (await read.GetAsync("accepted"u8.ToArray()))?.ToArray());
    }

    static ValueTask<IPantsDatabase> OpenAsync() => PantsDatabase.OpenAsync(
        PantsOpenOptions.InMemory().WithMemtableLimits(64 * 1024));

    static async Task WriteAsync(IPantsDatabase database, IPantsColumnFamily family, string key, byte[] value)
    {
        await using var write = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite);
        write.Put(TestBytes.FromString(key), value);
        await write.CommitAsync(PantsWriteOptions.Buffered);
    }
}
