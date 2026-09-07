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
