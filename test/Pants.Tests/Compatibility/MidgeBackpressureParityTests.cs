using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeBackpressureParityTests
{
    static readonly string[] DurableModes = ["local", "cloud"];

    [Fact]
    public async Task ShouldAcceptSmallTransactionsWhenEachFitsTheIntentPool()
    {
        foreach (var mode in DurableModes)
        {
            using var directory = new TemporaryDirectory();
            var options = DurableOptions(mode, directory.Path)
                .WithMemoryBudget(PantsMemoryBudget.FromBytes(128L * 1024 * 1024))
                .WithTransactionMemoryPool(1024 * 1024)
                .WithMemtableLimits(32L * 1024 * 1024);
            await using var database = await PantsDatabase.OpenAsync(options);
            var family = await database.ColumnFamilies.CreateAsync("test");

            for (var index = 0; index < 10_000; index++)
            {
                await using var transaction = await database.Transactions.BeginAsync(
                    family,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(
                    TestBytes.FromString($"key_{index:000000}"),
                    new byte[1024]);
                await transaction.CommitAsync(WriteOptions(mode));
            }
        }
    }

    [Fact]
    public async Task ShouldBoundPublishedL0WhenBackgroundCompactionIsDisabled()
    {
        const int l0FileCountTrigger = 4;
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithCompaction(new PantsCompactionConfiguration(
                L0FileCountTrigger: l0FileCountTrigger,
                BackgroundEnabled: false));
        await using var database = await PantsDatabase.OpenAsync(options);
        var family = await database.ColumnFamilies.CreateAsync("bounded-l0");
        var hardCeiling = l0FileCountTrigger +
            MemtableWritePressure.MaximumImmutableMemtablesPerColumnFamily + 1;
        var accepted = 0;
        var attempts = 0;

        while (accepted < hardCeiling * 2)
        {
            attempts++;
            Assert.True(attempts <= hardCeiling * 8, "L0 recovery must make progress.");
            var key = TestBytes.FromString($"generation-{accepted:00}");
            if (await TryCommitBufferedPutAsync(database, family, key))
            {
                accepted++;
                await database.Maintenance.FlushAsync(family);
            }
            else
            {
                Assert.True(
                    await database.Maintenance.WaitForWriteStallClearAsync(
                        family,
                        TimeSpan.FromSeconds(10)),
                    "Critical L0 pressure must clear without manual compaction.");
            }

            var layout = await database.Diagnostics.GetStorageLayoutAsync();
            var l0Files = layout.Levels
                .FirstOrDefault(static level => level.Level == 0)?.FileCount ?? 0;
            Assert.True(l0Files <= hardCeiling, "Published L0 exceeded its ceiling.");
        }

        Assert.Equal(hardCeiling * 2, accepted);
        await database.Maintenance.CompactAllAsync();
        var drainedLayout = await database.Diagnostics.GetStorageLayoutAsync();
        Assert.DoesNotContain(drainedLayout.Levels, static level => level.Level == 0);
        Assert.True(await TryCommitBufferedPutAsync(
            database,
            family,
            TestBytes.FromString("after-drain")));
    }

    [Fact]
    public async Task ShouldPublishNaturalFlushOrReportWriteStallDuringBoundedWrites()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(8L * 1024 * 1024))
            .WithTransactionMemoryPool(512 * 1024)
            .WithMemtableLimits(256 * 1024);
        await using var database = await PantsDatabase.OpenAsync(options);
        var family = await database.ColumnFamilies.CreateAsync("test");
        const int maxAttempts = 64;
        var totalWrites = 0;
        var totalStalls = 0;

        while (totalWrites + totalStalls < maxAttempts)
        {
            var key = TestBytes.FromString($"key_{totalWrites}");
            var value = new byte[8 * 1024];
            if (await TryCommitBufferedPutAsync(database, family, key, value))
            {
                totalWrites++;
            }
            else
            {
                totalStalls++;
                await Task.Delay(10);
            }
        }

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (totalStalls == 0 && (metrics.SstCount == 0 || metrics.WriteStalled))
        {
            Assert.False(
                deadline.IsCancellationRequested,
                $"Local pressure was not rejected or naturally flushed: writes={totalWrites}, stalls={totalStalls}, ssts={metrics.SstCount}, writeStalled={metrics.WriteStalled}.");
            await Task.Delay(10);
            metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        }

        Assert.True(
            totalStalls > 0 || (metrics.SstCount > 0 && !metrics.WriteStalled),
            $"Expected local mode to reject writes under hard pressure or relieve pressure via natural flush: writes={totalWrites}, stalls={totalStalls}, ssts={metrics.SstCount}, writeStalled={metrics.WriteStalled}.");
    }

    static PantsOpenOptions DurableOptions(string mode, string path) => mode switch
    {
        "local" => PantsOpenOptions.Local(path),
        "cloud" => PantsOpenOptions.SimulatedCloud(path, "pants-tests", "backpressure/"),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown storage mode.")
    };

    static PantsWriteOptions WriteOptions(string mode) =>
        mode == "cloud" ? PantsWriteOptions.CloudAsync : PantsWriteOptions.Buffered;

    static async ValueTask<bool> TryCommitBufferedPutAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        byte[] key,
        byte[]? value = null)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(key, value ?? TestBytes.FromString("value"));
        try
        {
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
            return true;
        }
        catch (PantsWriteStallException)
        {
            return false;
        }
    }
}
