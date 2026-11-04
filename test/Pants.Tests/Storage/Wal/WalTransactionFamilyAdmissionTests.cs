using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class WalTransactionFamilyAdmissionTests
{
    [Theory]
    [InlineData(PantsDurability.Sync)]
    [InlineData(PantsDurability.Buffered)]
    [InlineData(PantsDurability.BestEffort)]
    public async Task ShouldRejectMixedFamilyGroupBeforeAnyWalAuthorityEffectAndPermitLegalRetry(PantsDurability durability)
    {
        using var directory = new TemporaryDirectory();
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        var first = new ColumnFamilyIdentity(0, "default", RuntimeState.DefaultFamilyVersion);
        var side = new ColumnFamilyIdentity(1, "side", 0);
        using (var store = LocalDiskStore.Open(directory.Path, state))
        {
            store.CreateColumnFamily(side);
            using var legal = Payload(state, 1, (first, "not-applied"));
            using var illegal = Payload(state, 2, (first, "illegal-a"), (side, "illegal-b"));
            var firstSequence = SequenceSpace.CommitSequenceAfter(checked((ulong)store.CommitSequenceFloor), legal.Operations.Count);
            var lastSequence = SequenceSpace.CommitSequenceAfter(checked((ulong)firstSequence), illegal.Operations.Count);
            var walPath = WalTestFiles.ActivePath(directory.Path);
            var ledgerPath = Path.Combine(directory.Path, "wal", "LEDGER");
            var wal = await File.ReadAllBytesAsync(walPath);
            var ledger = await File.ReadAllBytesAsync(ledgerPath);

            Assert.Throws<PantsInvalidArgumentException>(() => store.AppendCommitGroup(
                [new WalCommitGroupEntry(legal, firstSequence), new WalCommitGroupEntry(illegal, lastSequence)],
                state.Sequence, durability, static () => { }));

            Assert.Equal(wal, await File.ReadAllBytesAsync(walPath));
            Assert.Equal(ledger, await File.ReadAllBytesAsync(ledgerPath));
            Assert.False(store.IsWalFenced);
            using var retry = Payload(state, 3, (side, "accepted"));
            var receipt = store.AppendCommit(retry, state.Sequence, PantsDurability.Sync);
            state.Sequence = receipt.CommitSequence;
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        var family = await reopened.ColumnFamilies.GetAsync("side");
        Assert.NotNull(family);
        await using var reader = await reopened.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly);
        Assert.Equal("value"u8.ToArray(), (await reader.GetAsync("accepted"u8.ToArray()))?.ToArray());
        Assert.Null(await reader.GetAsync("illegal-b"u8.ToArray()));
        await using var defaultReader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Null(await defaultReader.GetAsync("not-applied"u8.ToArray()));
        Assert.Null(await defaultReader.GetAsync("illegal-a"u8.ToArray()));
    }

    static CommitPayload Payload(RuntimeState state, long transactionId,
        params (ColumnFamilyIdentity Family, string Key)[] entries)
    {
        var operations = entries.Select((entry, index) => new TransactionIntentOperation(checked((ulong)index),
            CommitOperationKind.Put, entry.Family, TestBytes.FromString(entry.Key), null,
            "value"u8.ToArray(), null, null, false)).ToArray();
        return new CommitPayload(transactionId, PantsTransactionMode.ReadWrite,
            PantsConflictPolicy.LastWriteWins, DateTimeOffset.UnixEpoch, state.CreateVersion(),
            new TransactionOperationSource(null, operations, checked((ulong)operations.Length), DateTimeOffset.UnixEpoch), []);
    }
}
