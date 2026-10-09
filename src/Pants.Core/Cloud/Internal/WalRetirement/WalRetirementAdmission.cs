namespace Cntryl.Pants.Cloud.Internal.WalRetirement;

/// <summary>
///     Admits one ranged WAL read and its parse workspace against the shared maintenance pool.
///     Like SST publication it first enters the <see cref="MaintenanceMemoryGate" />, so a merge
///     never observes the reservation, but it holds the gate for one fetch only: retirement yields
///     the pool between reads instead of holding it for a whole segment. A read whose workspace
///     exceeds the pool is not admitted; the caller retains WAL authority and retries later.
/// </summary>
sealed class WalRetirementAdmission(ResourceBudget budget, MaintenanceMemoryGate gate)
{
    /// <summary>
    ///     A fetched range, the frame payload copied from it, the decoded record and its decoded
    ///     mutations coexist while a frame is checked.
    /// </summary>
    const int WorkspaceMultiplier = 4;

    public static long WorkspaceBytes(long fetchBytes) => checked(fetchBytes * WorkspaceMultiplier);

    /// <exception cref="PantsResourceLimitException">The workspace exceeds the whole pool.</exception>
    public async ValueTask<IDisposable> AdmitAsync(long fetchBytes, CancellationToken cancellationToken)
    {
        var bytes = WorkspaceBytes(fetchBytes);
        if (bytes > budget.Limit)
        {
            throw new PantsResourceLimitException(
                $"A {fetchBytes}-byte WAL retirement read needs {bytes} bytes of maintenance " +
                $"memory; the pool holds {budget.Limit}.");
        }

        var hold = await gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new Admission(budget.Reserve(bytes), hold);
        }
        catch
        {
            hold.Dispose();
            throw;
        }
    }

    sealed class Admission(IDisposable reservation, IDisposable hold) : IDisposable
    {
        public void Dispose()
        {
            reservation.Dispose();
            hold.Dispose();
        }
    }
}
