using Cntryl.Pants.Observability;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Transactions.Spill;

public sealed class TransactionSpillRetirementTests
{
    const string SpillFailureMetric = "pants.transactions.spill_delete_failures";

    static ColumnFamilyIdentity Family => new(0, "default", 0);

    /// <summary>
    ///     A run file that cannot be removed still occupies the local disk, so its budget charge must
    ///     survive the store's disposal and the failure must be observable.
    /// </summary>
    [Fact]
    public void ShouldRetainSpillChargeAndCountFailureWhenRunFilesCannotBeRemoved()
    {
        using var directory = new TemporaryDirectory();
        var ledger = new StorageBudgetLedger(new HybridStorageBudgetPolicy(1024 * 1024), () => 0);
        var remover = new ScriptedSpillFileRemover { FailRemovals = true };
        var retirement = new SpillRunRetirement(remover);
        using var measurements = new RuntimeMeterMeasurements(new HashSet<string>(StringComparer.Ordinal)
        {
            SpillFailureMetric
        });

        using (var store = new TransactionSpillStore(directory.Path, 1, Family, ledger, retirement))
        {
            store.WriteRun(CreateOperations(4));
        }

        Assert.True(ledger.ReservedBytes > 0);
        Assert.NotEmpty(TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path));
        Assert.True(measurements[SpillFailureMetric] > 0);
    }

    /// <summary>
    ///     Once a later removal succeeds the retained charge is released, and releasing it again on a
    ///     subsequent spill must not disturb the ledger.
    /// </summary>
    [Fact]
    public void ShouldReleaseRetainedSpillChargeOnceAfterALaterRemovalSucceeds()
    {
        using var directory = new TemporaryDirectory();
        var ledger = new StorageBudgetLedger(new HybridStorageBudgetPolicy(1024 * 1024), () => 0);
        var remover = new ScriptedSpillFileRemover { FailRemovals = true };
        var retirement = new SpillRunRetirement(remover);

        using (var failed = new TransactionSpillStore(directory.Path, 1, Family, ledger, retirement))
        {
            failed.WriteRun(CreateOperations(4));
        }

        Assert.True(ledger.ReservedBytes > 0);

        remover.FailRemovals = false;
        using (var next = new TransactionSpillStore(directory.Path, 2, Family, ledger, retirement))
        {
            next.WriteRun(CreateOperations(4));
        }

        Assert.Equal(0, ledger.ReservedBytes);
        Assert.Empty(TransactionSpillHardeningTestHarness.FindArtifacts(directory.Path));

        using (var later = new TransactionSpillStore(directory.Path, 3, Family, ledger, retirement))
        {
            later.WriteRun(CreateOperations(4));
            Assert.True(ledger.ReservedBytes > 0);
        }

        Assert.Equal(0, ledger.ReservedBytes);
    }

    static TransactionIntentOperation[] CreateOperations(int count) =>
        Enumerable.Range(0, count)
            .Select(index => new TransactionIntentOperation(
                (ulong)index,
                CommitOperationKind.Put,
                Family,
                BitConverter.GetBytes(index),
                null,
                new byte[64],
                null,
                null,
                false))
            .ToArray();

    /// <summary>
    ///     Fails removals on demand. Successful removals delegate to the production remover so the
    ///     files are really gone.
    /// </summary>
    sealed class ScriptedSpillFileRemover : ISpillFileRemover
    {
        readonly FileSystemSpillFileRemover _inner = new();

        public bool FailRemovals { get; set; }

        public void Remove(string path)
        {
            if (FailRemovals)
            {
                throw new IOException("Injected spill file removal failure.");
            }

            _inner.Remove(path);
        }
    }
}
