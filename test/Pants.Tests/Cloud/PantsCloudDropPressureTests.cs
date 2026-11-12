using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class PantsCloudDropPressureTests
{
    [Fact]
    public async Task ShouldWakeAnExistingWaiterWhenDiscardingACloudFamilyClearsGlobalPressure()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "drop-pressure/")
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(1024 * 1024))
            .WithMemtableLimits(4096)
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenAsync(options);
        var observer = await database.ColumnFamilies.CreateAsync("observer");
        var families = new List<IPantsColumnFamily>();
        for (var index = 0; index < 3; index++)
        {
            var family = await database.ColumnFamilies.CreateAsync($"pressure-{index}");
            families.Add(family);
            await using var write = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite);
            write.Put("key"u8.ToArray(), new byte[2800]);
            await write.CommitAsync(PantsWriteOptions.BestEffort);
        }

        Assert.False(await database.Maintenance.WaitForWriteStallClearAsync(observer, TimeSpan.Zero));
        var waiter = database.Maintenance.WaitForWriteStallClearAsync(observer, TimeSpan.FromSeconds(5)).AsTask();
        // The catalog query runs after the wait-status command on the same actor, so that command
        // has captured the existing pressure-change task before the drop can change the state.
        Assert.NotNull(await database.ColumnFamilies.GetAsync("observer"));
        Assert.False(waiter.IsCompleted);

        await database.ColumnFamilies.DropDiscardingUnflushedAsync(families[^1]);

        Assert.True(await database.Maintenance.WaitForWriteStallClearAsync(observer, TimeSpan.Zero));
        Assert.True(await waiter);
        Assert.NotNull(await database.ColumnFamilies.GetAsync("pressure-0"));
        Assert.NotNull(await database.ColumnFamilies.GetAsync("pressure-1"));
        Assert.Null(await database.ColumnFamilies.GetAsync("pressure-2"));
    }
}
