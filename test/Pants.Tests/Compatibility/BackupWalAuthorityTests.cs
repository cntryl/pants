using Cntryl.Pants.Storage.Internal;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class BackupWalAuthorityTests
{
    [Fact]
    public async Task ShouldCaptureOnlyLedgerNamedWalAndRestoreItsCommittedHistory()
    {
        using var directory = new TemporaryDirectory();
        var source = Path.Combine(directory.Path, "source");
        var artifact = Path.Combine(directory.Path, "backup");
        var restored = Path.Combine(directory.Path, "restored");
        var state = NewState();
        using (var store = LocalDiskStore.Open(source, state))
        {
            _ = store.AppendCommit(Payload(state), state, PantsDurability.Sync);
            _ = store.RotateActiveLocalWal();
            // The alias parses as the retained segment's ID, but only the canonical ledger name
            // is authoritative. Raw presence must not make this orphan part of a backup.
            var orphan = Path.Combine(store.RootPath, "wal", "1.wal");
            File.WriteAllBytes(orphan, "not WAL history"u8.ToArray());

            var paths = store.GetBackupFilePaths(false);

            Assert.DoesNotContain(orphan, paths);
            using var capture = BackupArtifactStore.PrepareCapture(source, artifact);
            using var pinned = await BackupArtifactStore.PinFilesAsync(capture.SourceRoot, paths,
                capture.ObjectsPath, CancellationToken.None);
            var manifest = await BackupArtifactStore.CompleteCaptureAsync(capture, pinned,
                checked((ulong)state.Sequence), PantsBackupStorageKind.Local, DateTimeOffset.UnixEpoch,
                NullPantsFailpointHandler.Instance, CancellationToken.None);
            Assert.DoesNotContain(manifest.Objects, static item => item.Path == "wal/1.wal");
            Assert.Equal("not WAL history"u8.ToArray(), File.ReadAllBytes(orphan));
        }

        await PantsDatabase.RestoreBackupAsync(artifact, PantsOpenOptions.Local(restored));
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(restored));
        await using var reader = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal("value"u8.ToArray(), (await reader.GetAsync("key"u8.ToArray()))?.ToArray());
    }

    [Fact]
    public void ShouldFailBackupCaptureWhenALedgerNamedSegmentIsMissing()
    {
        using var directory = new TemporaryDirectory();
        var state = NewState();
        using var store = LocalDiskStore.Open(directory.Path, state);
        _ = store.AppendCommit(Payload(state), state, PantsDurability.Sync);
        _ = store.RotateActiveLocalWal();
        var ledger = WalLedgerStore.LoadExisting(Path.Combine(store.RootPath, "wal"));
        var sealedId = Assert.Single(ledger.Members, static member => member.Sealed is not null).Open.Id;
        File.Delete(Path.Combine(store.RootPath, "wal", $"{sealedId:00000000000000000000}.wal"));

        Assert.Throws<PantsCorruptionException>(() => store.GetBackupFilePaths(false));
    }

    [Fact]
    public void ShouldFailBackupCaptureInsteadOfUsingCachedAuthorityWhenLedgerIsIncompatible()
    {
        using var directory = new TemporaryDirectory();
        using var store = LocalDiskStore.Open(directory.Path, NewState());
        var path = Path.Combine(directory.Path, "wal", "LEDGER");
        File.WriteAllBytes(path, "unsupported ledger"u8.ToArray());

        Assert.Throws<PantsCompatibilityException>(() => store.GetBackupFilePaths(false));
        Assert.Equal("unsupported ledger"u8.ToArray(), File.ReadAllBytes(path));
    }

    static RuntimeState NewState() => new(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());

    static CommitPayload Payload(RuntimeState state)
    {
        var family = new ColumnFamilyIdentity(0, "default", RuntimeState.DefaultFamilyVersion);
        var operation = new TransactionIntentOperation(0, CommitOperationKind.Put, family,
            "key"u8.ToArray(), null, "value"u8.ToArray(), null, null, false);
        return new CommitPayload(1, PantsTransactionMode.ReadWrite, PantsConflictPolicy.LastWriteWins,
            DateTimeOffset.UnixEpoch, state.CreateVersion(),
            new TransactionOperationSource(null, [operation], 1, DateTimeOffset.UnixEpoch), []);
    }
}
