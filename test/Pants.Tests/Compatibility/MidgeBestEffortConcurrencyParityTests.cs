using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeBestEffortConcurrencyParityTests
{
    const int WorkerCount = 5;
    const int BatchesPerWorker = 20;
    const int ValuesPerBatch = 50;

    [Fact]
    public async Task ShouldPreserveEveryAcknowledgedBestEffortValueAfterConcurrentWrites()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithMemtableLimits(8 * 1024 * 1024));
        var family = await database.ColumnFamilies.CreateAsync("test");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var workers = Enumerable.Range(0, WorkerCount).Select(worker => Task.Run(async () =>
        {
            await start.Task;
            for (var batch = 0; batch < BatchesPerWorker; batch++)
            {
                await using var transaction = await database.Transactions.BeginAsync(
                    family,
                    PantsTransactionMode.ReadWrite);
                for (var index = 0; index < ValuesPerBatch; index++)
                {
                    var key = $"stress_load_t{worker}_b{batch}_k{index:000}";
                    transaction.Put(TestBytes.FromString(key), TestBytes.FromString("stress_value"));
                }

                await transaction.CommitAsync(PantsWriteOptions.BestEffort);
            }
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(workers).WaitAsync(TestTimeouts.Expected);
        await database.Maintenance.FlushAsync(family);
        await database.Maintenance.CompactAllAsync();

        await using var snapshot = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        for (var worker = 0; worker < WorkerCount; worker++)
        {
            for (var batch = 0; batch < BatchesPerWorker; batch++)
            {
                for (var index = 0; index < ValuesPerBatch; index++)
                {
                    var key = $"stress_load_t{worker}_b{batch}_k{index:000}";
                    var value = await snapshot.GetAsync(TestBytes.FromString(key));
                    Assert.Equal("stress_value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(value)));
                }
            }
        }
    }
}
