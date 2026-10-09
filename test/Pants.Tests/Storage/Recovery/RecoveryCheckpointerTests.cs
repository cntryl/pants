namespace Cntryl.Pants.Storage.Recovery;

public sealed class RecoveryCheckpointerTests
{
    const long Kibibyte = 1024;

    [Fact]
    public void ShouldUseTheMemtableTargetWhenNoLocalStorageBudgetApplies()
    {
        var checkpointer = RecoveryCheckpointer.Create(64 * Kibibyte, null);

        Assert.Equal(64 * Kibibyte, checkpointer.TargetBytes);
        Assert.False(checkpointer.IsDue(64 * Kibibyte - 1));
        Assert.True(checkpointer.IsDue(64 * Kibibyte));
    }

    [Fact]
    public void ShouldCapTheCheckpointTargetAtHalfTheFreeLocalStorage()
    {
        var ledger = CreateLedger(maximumBytes: 1024 * Kibibyte, residentBytes: 512 * Kibibyte);

        var checkpointer = RecoveryCheckpointer.Create(16 * 1024 * Kibibyte, ledger);

        Assert.Equal(256 * Kibibyte, checkpointer.TargetBytes);
    }

    [Fact]
    public void ShouldChargeCheckpointStagingToTheLocalStorageLedgerUntilPublished()
    {
        var ledger = CreateLedger(maximumBytes: 1024 * Kibibyte, residentBytes: 0);
        var checkpointer = RecoveryCheckpointer.Create(64 * Kibibyte, ledger);
        var chargedDuringPublication = -1L;

        checkpointer.Publish(48 * Kibibyte, () => chargedDuringPublication = ledger.ReservedBytes);

        Assert.Equal(48 * Kibibyte, chargedDuringPublication);
        Assert.Equal(0, ledger.ReservedBytes);
    }

    [Fact]
    public void ShouldReleaseCheckpointStagingWhenPublicationFails()
    {
        var ledger = CreateLedger(maximumBytes: 1024 * Kibibyte, residentBytes: 0);
        var checkpointer = RecoveryCheckpointer.Create(64 * Kibibyte, ledger);

        Assert.Throws<IOException>(() => checkpointer.Publish(
            48 * Kibibyte,
            static () => throw new IOException("disk failed")));

        Assert.Equal(0, ledger.ReservedBytes);
    }

    [Fact]
    public void ShouldFailWithResourceLimitWhenOneTransactionExceedsTheMemoryWorkingSet()
    {
        var checkpointer = RecoveryCheckpointer.Create(Kibibyte, null);

        Assert.Throws<PantsResourceLimitException>(() =>
            checkpointer.RequireTransactionFits([CreatePut(16 * Kibibyte)]));
    }

    [Fact]
    public void ShouldFailWithNoSpaceWhenLocalResidueLeavesNoCheckpointCapacity()
    {
        var ledger = CreateLedger(maximumBytes: 1024 * Kibibyte, residentBytes: 1024 * Kibibyte);
        var checkpointer = RecoveryCheckpointer.Create(64 * Kibibyte, ledger);

        checkpointer.RequireTransactionFits([]);
        Assert.Throws<PantsNoSpaceException>(() =>
            checkpointer.RequireTransactionFits([CreatePut(1)]));
    }

    static StorageBudgetLedger CreateLedger(long maximumBytes, long residentBytes) =>
        new(new HybridStorageBudgetPolicy(maximumBytes), () => residentBytes);

    static WalMutation CreatePut(long valueBytes) => new(
        0,
        WalOperation.Put,
        "key"u8.ToArray(),
        new byte[valueBytes],
        1,
        null,
        null);
}
