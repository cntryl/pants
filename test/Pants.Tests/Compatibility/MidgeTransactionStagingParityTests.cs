using System.Buffers.Binary;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeTransactionStagingParityTests
{
    const int ValueLength = 128;
    const int AcceptedRows = 64;
    const int OversizedBatchRows = 12_500;
    const long LocalStorageBudgetBytes = 2L * 1024 * 1024;

    [Fact]
    public async Task ShouldPreserveDurableRowsWhenOversizedBatchIsRejected()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);

        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = database.ColumnFamilies.DefaultFamily;
            await WriteRowsAsync(database, family, 0, AcceptedRows, PantsWriteOptions.BestEffort);
            await database.Maintenance.FlushAsync(family);
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();
            PantsNoSpaceException? rejection = null;
            var rejectedValue = new byte[ValueLength];
            Array.Fill(rejectedValue, (byte)'r');

            await using (var transaction = await database.Transactions.BeginAsync(
                             family,
                             PantsTransactionMode.ReadWrite))
            {
                try
                {
                    for (var index = 0; index < OversizedBatchRows; index++)
                    {
                        transaction.Put(CreateKey(index), rejectedValue);
                    }

                    await transaction.CommitAsync(PantsWriteOptions.BestEffort);
                }
                catch (PantsNoSpaceException exception)
                {
                    rejection = exception;
                }
            }

            Assert.NotNull(rejection);
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(before.CurrentSequence, after.CurrentSequence);
            Assert.Equal(before.WalAppendCount, after.WalAppendCount);
            Assert.False(Directory.Exists(Path.Combine(directory.Path, "txn")));
            Assert.Equal(
                0,
                Assert.IsType<PantsHybridStorageBudgetSnapshot>(after.HybridStorageBudget)
                    .Usage.TransactionSpillBytes);
            await AssertRowsAsync(database, AcceptedRows);
            await database.ShutdownAsync(TestTimeouts.Expected);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertRowsAsync(reopened, AcceptedRows);
        await reopened.ShutdownAsync(TestTimeouts.Expected);
    }

    [Fact]
    public async Task ShouldRejectOversizedValueWithoutPublishingAnyPartOfTransaction()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var before = await database.Diagnostics.GetRuntimeMetricsAsync();
            PantsNoSpaceException? rejection = null;

            await using (var transaction = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                try
                {
                    transaction.Put(CreateKey(0), CreateValue(0));
                    transaction.Put(CreateKey(1), new byte[2 * 1024 * 1024]);
                    await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
                }
                catch (PantsNoSpaceException exception)
                {
                    rejection = exception;
                }
            }

            Assert.NotNull(rejection);
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(before.CurrentSequence, after.CurrentSequence);
            Assert.Equal(before.WalAppendCount, after.WalAppendCount);
            Assert.False(Directory.Exists(Path.Combine(directory.Path, "txn")));
            Assert.Equal(
                0,
                Assert.IsType<PantsHybridStorageBudgetSnapshot>(after.HybridStorageBudget)
                    .Usage.TransactionSpillBytes);
            await AssertRowsAsync(database, 0);
            await database.ShutdownAsync(TestTimeouts.Expected);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertRowsAsync(reopened, 0);
        await reopened.ShutdownAsync(TestTimeouts.Expected);
    }

    [Fact]
    public async Task ShouldRecoverEveryConcurrentBestEffortWriteAfterCompletedFamilyFlush()
    {
        const int writerCount = 4;
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path);

        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = database.ColumnFamilies.DefaultFamily;
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writers = Enumerable.Range(0, writerCount)
                .Select(writer => Task.Run(async () =>
                {
                    await start.Task;
                    await WriteRowsAsync(
                        database,
                        family,
                        writer * AcceptedRows,
                        AcceptedRows,
                        PantsWriteOptions.BestEffort);
                }))
                .ToArray();

            start.SetResult();
            await Task.WhenAll(writers).WaitAsync(TestTimeouts.Expected);
            await database.Maintenance.FlushAsync(family);
            await AssertRowsAsync(database, writerCount * AcceptedRows);
            await database.ShutdownAsync(TestTimeouts.Expected);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertRowsAsync(reopened, writerCount * AcceptedRows);
        await reopened.ShutdownAsync(TestTimeouts.Expected);
    }

    static PantsOpenOptions CreateOptions(string path) =>
        PantsOpenOptions.SimulatedCloud(path, "staging-tests", "transactions")
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(16L * 1024 * 1024))
            .WithSimulatedCloudLocalStorageBudget(LocalStorageBudgetBytes)
            .WithMemtableLimits(256 * 1024)
            .WithTransactionMemoryPool(128 * 1024)
            .WithBackgroundCompaction(false);

    static async Task WriteRowsAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        int start,
        int count,
        PantsWriteOptions writeOptions)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        for (var index = start; index < start + count; index++)
        {
            transaction.Put(CreateKey(index), CreateValue(index));
        }

        await transaction.CommitAsync(writeOptions);
    }

    static async Task AssertRowsAsync(IPantsDatabase database, int expectedCount)
    {
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await using var scan = await reader.ScanAsync(new PantsScanQuery());
        var entries = new List<(byte[] Key, byte[] Value)>();
        await foreach (var entry in scan)
        {
            entries.Add((entry.Key.ToArray(), entry.Value.ToArray()));
        }

        Assert.Equal(expectedCount, entries.Count);
        for (var index = 0; index < expectedCount; index++)
        {
            Assert.Equal(CreateKey(index), entries[index].Key);
            Assert.Equal(CreateValue(index), entries[index].Value);
        }
    }

    static byte[] CreateKey(int index)
    {
        var key = new byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(8), checked((ulong)index));
        return key;
    }

    static byte[] CreateValue(int index)
    {
        var value = new byte[ValueLength];
        Array.Fill(value, checked((byte)(index % 251)));
        return value;
    }
}
