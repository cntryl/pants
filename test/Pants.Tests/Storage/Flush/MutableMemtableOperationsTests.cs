namespace Cntryl.Pants.Tests.Storage.Flush;

public sealed class MutableMemtableOperationsTests
{
    [Fact]
    public void ShouldKeepDetachedGenerationFrozenGivenSubsequentFamilyWrites()
    {
        var operations = new MutableMemtableOperations();
        operations.Add(CreateMutation(1, 1));
        operations.Add(CreateMutation(2, 2));

        var frozen = operations.DetachFamily(1);
        operations.Add(CreateMutation(1, 3));

        Assert.Equal([1UL], frozen.Select(static mutation => mutation.Sequence));
        Assert.Equal([2UL, 3UL], operations.SnapshotAll().Select(static mutation => mutation.Sequence));
    }

    [Fact]
    public void ShouldRestoreAppendOrderGivenGroupedWalRollback()
    {
        var operations = new MutableMemtableOperations();
        operations.Add(CreateMutation(1, 1));
        operations.Add(CreateMutation(2, 2));
        operations.Add(CreateMutation(1, 3));

        operations.TruncateAfter(1);

        Assert.Equal([1UL], operations.SnapshotAll().Select(static mutation => mutation.Sequence));
        Assert.Equal(1UL, operations.LastSequence);
    }

    static WalMutation CreateMutation(uint columnFamilyId, ulong sequence) =>
        new(
            columnFamilyId,
            WalOperation.Put,
            [(byte)sequence],
            [0x01],
            sequence,
            null,
            null);
}
