namespace Cntryl.Pants.Storage;

public sealed class StorageBudgetLedgerTests
{
    const long Budget = 1000;

    /// <summary>
    ///     The defect this type exists to fix: a watermark read against bytes already on disk lets
    ///     several in-flight operations each pass their own check before any of them lands.
    /// </summary>
    [Fact]
    public void ShouldChargeOutstandingReservationsAgainstTheBudget()
    {
        var resident = 0L;
        var ledger = new StorageBudgetLedger(new HybridStorageBudgetPolicy(Budget), () => resident);

        using var reservation = ledger.Reserve(StorageAdmissionKind.Flush, 400);

        Assert.Equal(400, ledger.ReservedBytes);
        Assert.Equal(400, ledger.ChargedBytes);
    }

    [Fact]
    public void ShouldRejectConcurrentReservationsThatTogetherOverrunTheBudget()
    {
        var resident = 0L;
        var ledger = new StorageBudgetLedger(new HybridStorageBudgetPolicy(Budget), () => resident);

        using var first = ledger.Reserve(StorageAdmissionKind.Hydration, 600);

        Assert.Throws<PantsNoSpaceException>(() =>
            ledger.Reserve(StorageAdmissionKind.Hydration, 600));
    }

    [Fact]
    public void ShouldReleaseTheFullEstimateWhenAReservationIsAbandoned()
    {
        var resident = 0L;
        var ledger = new StorageBudgetLedger(new HybridStorageBudgetPolicy(Budget), () => resident);

        using (ledger.Reserve(StorageAdmissionKind.Compaction, 500))
        {
            Assert.Equal(500, ledger.ReservedBytes);
        }

        Assert.Equal(0, ledger.ReservedBytes);
    }

    /// <summary>
    ///     Once the operation has published, its bytes are resident and the estimate is redundant;
    ///     leaving it charged would keep headroom locked up after the fact.
    /// </summary>
    [Fact]
    public void ShouldChargeOnlyResidentBytesOnceTheReservationIsReleased()
    {
        var resident = 0L;
        var ledger = new StorageBudgetLedger(new HybridStorageBudgetPolicy(Budget), () => resident);

        using (ledger.Reserve(StorageAdmissionKind.Flush, 500))
        {
            resident = 120;
            Assert.Equal(620, ledger.ChargedBytes);
        }

        Assert.Equal(0, ledger.ReservedBytes);
        Assert.Equal(120, ledger.ChargedBytes);
    }

    [Fact]
    public void ShouldReleaseOnlyOnce()
    {
        var resident = 0L;
        var ledger = new StorageBudgetLedger(new HybridStorageBudgetPolicy(Budget), () => resident);

        var reservation = ledger.Reserve(StorageAdmissionKind.Flush, 500);
        reservation.Dispose();
        reservation.Dispose();

        Assert.Equal(0, ledger.ReservedBytes);
    }

    /// <summary>
    ///     Flush and compaction are what release space, so refusing them at the same point user
    ///     writes are refused would wedge a full database: writes blocked, and the work that would
    ///     unblock them blocked too.
    /// </summary>
    [Fact]
    public void ShouldAdmitMaintenanceAboveTheWatermarkThatRefusesUserWrites()
    {
        var resident = 985L;
        var ledger = new StorageBudgetLedger(new HybridStorageBudgetPolicy(Budget), () => resident);

        Assert.Throws<PantsNoSpaceException>(() =>
            ledger.Reserve(StorageAdmissionKind.Hydration, 1));

        using var compaction = ledger.Reserve(StorageAdmissionKind.Compaction, 10);
        Assert.Equal(10, ledger.ReservedBytes);
    }

    [Fact]
    public void ShouldRefuseMaintenanceThatWouldExceedTheBudgetOutright()
    {
        var resident = 985L;
        var ledger = new StorageBudgetLedger(new HybridStorageBudgetPolicy(Budget), () => resident);

        Assert.Throws<PantsNoSpaceException>(() =>
            ledger.Reserve(StorageAdmissionKind.Compaction, 100));
    }
}
