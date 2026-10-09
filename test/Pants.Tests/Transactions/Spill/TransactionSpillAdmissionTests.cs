using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Transactions.Spill;

public sealed class TransactionSpillAdmissionTests
{
    /// <summary>
    ///     Spill files live on the same local disk as the WAL and SSTs but sit outside the resident
    ///     figure the budget is measured from, so a degenerate transaction could previously consume
    ///     the volume with nothing accounting for it.
    /// </summary>
    [Fact]
    public void ShouldRefuseSpillBeyondTheLocalStorageBudget()
    {
        using var directory = new TemporaryDirectory();
        var ledger = new StorageBudgetLedger(new HybridStorageBudgetPolicy(4096), () => 0);
        using var store = new TransactionSpillStore(
            directory.Path,
            1,
            new ColumnFamilyIdentity(0, "default", 0),
            ledger);

        Assert.Throws<PantsNoSpaceException>(() => store.WriteRun(CreateOperations(512)));
    }

    [Fact]
    public void ShouldReleaseSpillChargeWhenTheStoreIsDisposed()
    {
        using var directory = new TemporaryDirectory();
        var ledger = new StorageBudgetLedger(new HybridStorageBudgetPolicy(1024 * 1024), () => 0);
        using (var store = new TransactionSpillStore(
                   directory.Path,
                   1,
                   new ColumnFamilyIdentity(0, "default", 0),
                   ledger))
        {
            store.WriteRun(CreateOperations(4));
            Assert.True(ledger.ReservedBytes > 0);
        }

        Assert.Equal(0, ledger.ReservedBytes);
    }

    [Fact]
    public void ShouldSpillWithoutALedgerWhenNoLocalBudgetApplies()
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionSpillStore(
            directory.Path,
            1,
            new ColumnFamilyIdentity(0, "default", 0),
            null);

        store.WriteRun(CreateOperations(4));

        Assert.True(store.HasRuns);
    }

    /// <summary>
    ///     The run file repeats every range's bounds in its range index and every sixteenth key in its
    ///     sparse index. Those copies are real disk bytes, so a spill whose index duplication pushes it
    ///     past the budget must be refused rather than admitted on the size of its operations alone.
    /// </summary>
    [Fact]
    public void ShouldRefuseSpillWhoseIndexKeyDuplicationExceedsTheLocalStorageBudget()
    {
        using var directory = new TemporaryDirectory();
        var ledger = new StorageBudgetLedger(new HybridStorageBudgetPolicy(400_000), () => 0);
        using var store = new TransactionSpillStore(
            directory.Path,
            1,
            new ColumnFamilyIdentity(0, "default", 0),
            ledger);

        Assert.Throws<PantsNoSpaceException>(() => store.WriteRun(CreateIndexedOperations(0, 64, 4096)));
    }

    [Fact]
    public void ShouldHoldLedgerChargeAtLeastTheSizeOfTheRunAndRangeFilesItWrites()
    {
        using var directory = new TemporaryDirectory();
        var ledger = new StorageBudgetLedger(new HybridStorageBudgetPolicy(64 * 1024 * 1024), () => 0);
        using var store = new TransactionSpillStore(
            directory.Path,
            1,
            new ColumnFamilyIdentity(0, "default", 0),
            ledger);

        store.WriteRun(CreateIndexedOperations(200, 64, 4096));

        var writtenBytes = Directory.EnumerateFiles(Path.Combine(directory.Path, "txn"))
            .Sum(static path => new FileInfo(path).Length);
        Assert.True(
            ledger.ReservedBytes >= writtenBytes,
            $"Ledger holds {ledger.ReservedBytes} bytes but the spill wrote {writtenBytes}.");
    }

    static TransactionIntentOperation[] CreateIndexedOperations(int puts, int ranges, int endLength)
    {
        var family = new ColumnFamilyIdentity(0, "default", 0);
        var operations = new List<TransactionIntentOperation>(puts + ranges);
        for (var index = 0; index < puts; index++)
        {
            operations.Add(new TransactionIntentOperation(
                (ulong)operations.Count,
                CommitOperationKind.Put,
                family,
                BitConverter.GetBytes(index),
                null,
                new byte[64],
                null,
                null,
                false));
        }

        for (var index = 0; index < ranges; index++)
        {
            operations.Add(new TransactionIntentOperation(
                (ulong)operations.Count,
                CommitOperationKind.DeleteRange,
                family,
                BitConverter.GetBytes(index + puts),
                new byte[endLength],
                null,
                null,
                null,
                false));
        }

        return operations.ToArray();
    }

    static TransactionIntentOperation[] CreateOperations(int count) =>
        Enumerable.Range(0, count)
            .Select(index => new TransactionIntentOperation(
                (ulong)index,
                CommitOperationKind.Put,
                new ColumnFamilyIdentity(0, "default", 0),
                BitConverter.GetBytes(index),
                null,
                new byte[64],
                null,
                null,
                false))
            .ToArray();
}
