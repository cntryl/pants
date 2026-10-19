namespace Cntryl.Pants.Cloud.Internal;

/// <summary>
///     Reserves the encoded catalog copies and their parse workspace before reading either copy.
///     A catalog's decoded representation and the immutable byte snapshots can coexist while the
///     caller checks or publishes catalog authority, so the charge remains live until the returned
///     <see cref="WalCatalogRead" /> is disposed. The exclusive gate is released after parsing;
///     callers may then perform other gated maintenance reads without recursively waiting on it.
/// </summary>
sealed class ProviderWalCatalogReadAdmission(ResourceBudget budget, MaintenanceMemoryGate gate)
{
    public const int ReadRangeBytes = 64 * 1024;

    const int ParseWorkspaceMultiplier = 16;
    const long StandaloneBudgetBytes = 64L * 1024 * 1024;

    public static ProviderWalCatalogReadAdmission Standalone() => new(
        new ResourceBudget(StandaloneBudgetBytes),
        new MaintenanceMemoryGate());

    public async ValueTask<ProviderWalCatalogReadLease?> AdmitAsync(
        CloudObjectMetadata? primary,
        CloudObjectMetadata? mirror,
        CancellationToken cancellationToken,
        bool waitForMaintenanceGate = true)
    {
        var bytes = WorkspaceBytes(primary?.SizeBytes ?? 0, mirror?.SizeBytes ?? 0);
        if (bytes == 0)
        {
            return null;
        }

        if (bytes > budget.Limit)
        {
            throw new PantsResourceLimitException(
                $"Reading both cloud WAL catalog copies needs {bytes} bytes of maintenance " +
                $"memory; the pool holds {budget.Limit}.");
        }

        if (!waitForMaintenanceGate)
        {
            if (!budget.TryReserve(bytes, out var foregroundReservation))
            {
                throw new PantsResourceLimitException(
                    $"Reading both cloud WAL catalog copies needs {bytes} bytes of maintenance " +
                    $"memory, but the shared pool is currently occupied.");
            }

            return new ProviderWalCatalogReadLease(foregroundReservation, null);
        }

        var hold = await gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new ProviderWalCatalogReadLease(budget.Reserve(bytes), hold);
        }
        catch
        {
            hold.Dispose();
            throw;
        }
    }

    static long WorkspaceBytes(ulong primaryBytes, ulong mirrorBytes)
    {
        try
        {
            var encodedBytes = checked((long)primaryBytes + (long)mirrorBytes);
            return checked(encodedBytes * ParseWorkspaceMultiplier);
        }
        catch (OverflowException exception)
        {
            throw new PantsResourceLimitException(
                "Cloud WAL catalog sizes exceed the maintenance memory accounting range.",
                exception);
        }
    }
}

sealed class ProviderWalCatalogReadLease(IDisposable reservation, IDisposable? gateHold) : IDisposable
{
    IDisposable? _gateHold = gateHold;
    IDisposable? _reservation = reservation;

    public void ReleaseGate() => Interlocked.Exchange(ref _gateHold, null)?.Dispose();

    public void Dispose()
    {
        ReleaseGate();
        Interlocked.Exchange(ref _reservation, null)?.Dispose();
    }
}
