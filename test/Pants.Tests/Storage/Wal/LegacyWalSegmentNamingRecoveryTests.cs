using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class LegacyWalSegmentNamingRecoveryTests
{
    static readonly ColumnFamilyIdentity DefaultFamily = new(
        0,
        "default",
        RuntimeState.DefaultFamilyVersion);

    [Fact]
    public async Task ShouldRejectLedgerOwnedSegmentRenamedToLegacyWalFileName()
    {
        using var directory = new TemporaryDirectory();
        var telemetry = new RuntimeTelemetry();
        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            telemetry);
        using (var store = LocalDiskStore.Open(directory.Path, state))
        {
            _ = store.AppendCommit(
                CreateCommitPayload(state),
                state,
                PantsDurability.Sync);
            _ = store.RotateActiveLocalWal();
        }

        var walDirectory = Path.Combine(directory.Path, "wal");
        var ledger = WalLedgerStore.LoadExisting(walDirectory);
        var member = Assert.Single(ledger.Members, static member => member.Sealed is not null);
        var sealedSegment = Path.Combine(walDirectory, $"{member.Open.Id:00000000000000000000}.wal");
        var original = await File.ReadAllBytesAsync(sealedSegment);
        var legacyPath = Path.Combine(walDirectory, "wal_000042.log");
        File.Move(sealedSegment, legacyPath);

        await Assert.ThrowsAsync<PantsCorruptionException>(() => PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path)).AsTask());
        Assert.Equal(original, await File.ReadAllBytesAsync(legacyPath));
    }

    static CommitPayload CreateCommitPayload(RuntimeState state)
    {
        var operation = new TransactionIntentOperation(
            0,
            CommitOperationKind.Put,
            DefaultFamily,
            "legacy-key"u8.ToArray(),
            null,
            "legacy-value"u8.ToArray(),
            null,
            null,
            false);
        var source = new TransactionOperationSource(
            null,
            [operation],
            1,
            DateTimeOffset.UnixEpoch);
        return new CommitPayload(
            1,
            PantsTransactionMode.ReadWrite,
            PantsConflictPolicy.LastWriteWins,
            DateTimeOffset.UnixEpoch,
            state.CreateVersion(),
            source,
            []);
    }
}
