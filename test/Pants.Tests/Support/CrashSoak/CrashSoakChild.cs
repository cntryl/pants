using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Support.CrashSoak;

/// <summary>
///     Runs one cycle's workload inside a child process, ledgering every operation, and kills the
///     process at the planned point. It never returns.
/// </summary>
static class CrashSoakChild
{
    /// <summary>Stands in for the open in the ledger, so an open failure is reported like any other error.</summary>
    static readonly CrashSoakOperation OpenOperation = new(
        -1,
        -1,
        CrashSoakOperationKind.Flush,
        PantsDurability.Sync,
        []);

    public static async Task RunAsync(CrashSoakChildSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var scenario = spec.Scenario;
        var cycle = CrashSoakWorkload.CreateCycle(scenario, spec.Cycle);
        var ledger = new CrashSoakLedgerWriter(CrashSoakStorage.LedgerPath(spec.Root, spec.Cycle));
        var killSwitch = new CrashSoakKillSwitch(ledger, cycle.CrashPlan);
        IPantsDatabase database;
        try
        {
            database = await PantsDatabase.OpenForTestingAsync(
                CrashSoakStorage.CreateOptions(scenario, spec.Root, spec.CloudPrefix),
                CrashSoakStorage.CreateDependencies(CrashSoakStorage.ChildGeneration(spec.Cycle), killSwitch));
        }
        catch (Exception exception)
        {
            ledger.Errored(OpenOperation, exception);
            killSwitch.Kill("open failed");
            throw;
        }

        var lanes = cycle.Lanes
            .Select(lane => Task.Run(() => RunLaneAsync(database, lane, ledger, killSwitch)))
            .ToArray();
        await Task.WhenAll(lanes);
        ledger.Completed();
        killSwitch.Kill("workload complete");
    }

    static async Task RunLaneAsync(
        IPantsDatabase database,
        IReadOnlyList<CrashSoakOperation> lane,
        CrashSoakLedgerWriter ledger,
        CrashSoakKillSwitch killSwitch)
    {
        for (var index = 0; index < lane.Count; index++)
        {
            var operation = lane[index];
            try
            {
                switch (operation.Kind)
                {
                    case CrashSoakOperationKind.Flush:
                        await RunMaintenanceAsync(
                            operation,
                            ledger,
                            () => database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily));
                        break;
                    case CrashSoakOperationKind.Compact:
                        await RunMaintenanceAsync(operation, ledger, () => database.Maintenance.CompactAllAsync());
                        break;
                    case CrashSoakOperationKind.Commit:
                        await using (var transaction = await BeginAsync(database, operation))
                        {
                            await CommitAsync(transaction, operation, ledger, killSwitch);
                        }

                        break;
                    case CrashSoakOperationKind.Rollback:
                        await using (var transaction = await BeginAsync(database, operation))
                        {
                            ledger.Dispatched(operation);
                            await transaction.RollbackAsync();
                            ledger.Failed(operation, "rolled back");
                        }

                        break;
                    case CrashSoakOperationKind.AssertionFailure:
                        await using (var transaction = await BeginAsync(database, operation))
                        {
                            transaction.AssertValue(
                                TestBytes.FromString(operation.Mutations[0].Key),
                                TestBytes.FromString(CrashSoakWorkload.NeverWritten));
                            await CommitExpectingConflictAsync(transaction, operation, ledger, killSwitch);
                        }

                        break;
                    case CrashSoakOperationKind.ConflictWinner:
                        // The loser is attributed its own errors, so it is consumed here.
                        operation = lane[++index];
                        await RunConflictAsync(database, lane[index - 1], operation, ledger, killSwitch);
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected lane operation {operation}.");
                }
            }
            catch (ConflictWinnerException exception)
            {
                ledger.Errored(exception.Winner, exception.InnerException!);
                killSwitch.Kill($"unexpected exception in {exception.Winner}");
            }
            catch (Exception exception)
            {
                ledger.Errored(operation, exception);
                killSwitch.Kill($"unexpected exception in {operation}");
            }
        }
    }

    static async Task RunMaintenanceAsync(
        CrashSoakOperation operation,
        CrashSoakLedgerWriter ledger,
        Func<ValueTask> maintenance)
    {
        ledger.Dispatched(operation);
        try
        {
            await maintenance();
            ledger.Acked(operation);
        }
        catch (Exception exception) when (IsBackPressure(exception))
        {
            ledger.Failed(operation, exception.GetType().Name);
        }
    }

    static async Task RunConflictAsync(
        IPantsDatabase database,
        CrashSoakOperation winner,
        CrashSoakOperation loser,
        CrashSoakLedgerWriter ledger,
        CrashSoakKillSwitch killSwitch)
    {
        await using var loserTransaction = await BeginAsync(database, loser);
        loserTransaction.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
        bool winnerAcked;
        try
        {
            await using var winnerTransaction = await BeginAsync(database, winner);
            winnerAcked = await CommitAsync(winnerTransaction, winner, ledger, killSwitch);
        }
        catch (Exception exception)
        {
            throw new ConflictWinnerException(winner, exception);
        }

        // Without a committed winner nothing dooms the loser, so it is abandoned undispatched.
        if (winnerAcked)
        {
            await CommitExpectingConflictAsync(loserTransaction, loser, ledger, killSwitch);
        }
    }

    static async Task<IPantsTransaction> BeginAsync(IPantsDatabase database, CrashSoakOperation operation)
    {
        var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        foreach (var mutation in operation.Mutations)
        {
            mutation.ApplyTo(transaction);
        }

        return transaction;
    }

    /// <summary>
    ///     Commits and ledgers the outcome. Back-pressure is an authoritative rejection, so it is a
    ///     failure the verifier holds the engine to; any other exception propagates as unexpected.
    /// </summary>
    static async Task<bool> CommitAsync(
        IPantsTransaction transaction,
        CrashSoakOperation operation,
        CrashSoakLedgerWriter ledger,
        CrashSoakKillSwitch killSwitch)
    {
        ledger.Dispatched(operation);
        var commit = transaction.CommitAsync(ToWriteOptions(operation.Durability)).AsTask();
        if (killSwitch.ShouldKillAfterDispatch())
        {
            killSwitch.Kill($"dispatched op {operation.Id}");
        }

        try
        {
            await commit;
        }
        catch (Exception exception) when (IsBackPressure(exception))
        {
            ledger.Failed(operation, exception.GetType().Name);
            return false;
        }

        ledger.Acked(operation);
        return true;
    }

    static async Task CommitExpectingConflictAsync(
        IPantsTransaction transaction,
        CrashSoakOperation operation,
        CrashSoakLedgerWriter ledger,
        CrashSoakKillSwitch killSwitch)
    {
        try
        {
            await CommitAsync(transaction, operation, ledger, killSwitch);
        }
        catch (PantsWriteConflictException exception)
        {
            ledger.Failed(operation, exception.GetType().Name);
        }
    }

    static bool IsBackPressure(Exception exception) =>
        exception is PantsWriteStallException or PantsBusyException;

    static PantsWriteOptions ToWriteOptions(PantsDurability durability) => durability switch
    {
        PantsDurability.Sync => PantsWriteOptions.Sync,
        PantsDurability.Buffered => PantsWriteOptions.Buffered,
        PantsDurability.BestEffort => PantsWriteOptions.BestEffort,
        PantsDurability.CloudAsync => PantsWriteOptions.CloudAsync,
        PantsDurability.CloudStrict => PantsWriteOptions.CloudStrict,
        _ => throw new ArgumentOutOfRangeException(nameof(durability), durability, "Unknown durability.")
    };

    sealed class ConflictWinnerException(CrashSoakOperation winner, Exception inner)
        : Exception($"The conflict winner {winner} failed.", inner)
    {
        public CrashSoakOperation Winner { get; } = winner;
    }
}
