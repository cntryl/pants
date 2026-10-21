using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeMemoryManagementParityTests
{
    static readonly string[] StorageModes = ["memory", "local", "cloud"];

    [Fact]
    public async Task ShouldPreserveSmallCommitsWhenTheTransactionPoolIsSmall()
    {
        foreach (var mode in StorageModes)
        {
            using var directory = new TemporaryDirectory();
            var options = OpenOptions(mode, directory.Path)
                .WithMemoryBudget(PantsMemoryBudget.FromBytes(256L * 1024 * 1024))
                .WithTransactionMemoryPool(64 * 1024)
                .WithMemtableLimits(64L * 1024 * 1024);
            await using var database = await PantsDatabase.OpenAsync(options);
            var family = await database.ColumnFamilies.CreateAsync("test");
            var value = new byte[1024];
            Array.Fill(value, (byte)'x');
            var writeCount = 0;

            for (var index = 0; index < 1000; index++)
            {
                var key = TestBytes.FromString($"key_{index:00000000}");
                await using var transaction = await database.Transactions.BeginAsync(
                    family,
                    PantsTransactionMode.ReadWrite);
                transaction.Put(key, value);
                try
                {
                    await transaction.CommitAsync(WriteOptions(mode));
                    writeCount++;
                }
                catch (PantsWriteStallException)
                {
                    break;
                }

                if (index % 10 == 0)
                {
                    await database.Maintenance.FlushAsync(family);
                }
            }

            await database.Maintenance.FlushAsync(family);
            if (mode != "memory")
            {
                await database.Maintenance.CompactAllAsync();
            }

            var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.True(writeCount > 0, $"The {mode} mode accepted no transactions.");
            if (mode != "memory")
            {
                Assert.NotEqual(PantsEngineHealth.WriteStalled, metrics.Health);
            }

            await using var reader = await database.Transactions.BeginAsync(
                family,
                PantsTransactionMode.ReadOnly);
            for (var index = 0; index < writeCount; index++)
            {
                var key = TestBytes.FromString($"key_{index:00000000}");
                Assert.Equal(value, (await reader.GetAsync(key))?.ToArray());
            }
        }
    }

    [Fact]
    public async Task ShouldCompleteShutdownWhenWalWriterDrops()
    {
        foreach (var mode in StorageModes)
        {
            using var directory = new TemporaryDirectory();
            await using var database = await PantsDatabase.OpenAsync(OpenOptions(mode, directory.Path));
            var family = await database.ColumnFamilies.CreateAsync("test");
            await using var transaction = await database.Transactions.BeginAsync(
                family,
                PantsTransactionMode.ReadWrite);
            transaction.Put(TestBytes.FromString("key"), TestBytes.FromString("value"));
            await transaction.CommitAsync(WriteOptions(mode));

            await database.ShutdownAsync(TimeSpan.FromSeconds(10));
        }
    }

    static PantsOpenOptions OpenOptions(string mode, string path) =>
        mode switch
        {
            "memory" => PantsOpenOptions.InMemory(),
            "local" => PantsOpenOptions.Local(path),
            "cloud" => PantsOpenOptions.SimulatedCloud(path, "pants-tests", "memory-management-parity/"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown storage mode.")
        };

    static PantsWriteOptions WriteOptions(string mode) =>
        mode == "cloud" ? PantsWriteOptions.CloudAsync : PantsWriteOptions.Buffered;
}
