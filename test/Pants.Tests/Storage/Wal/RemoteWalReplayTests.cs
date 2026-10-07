using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class RemoteWalReplayTests
{
    static readonly ColumnFamilyIdentity DefaultFamily = new(
        0,
        "default",
        RuntimeState.DefaultFamilyVersion);

    [Fact]
    public void ShouldRecoverCommitFromRemoteOnlySegmentWithoutALocalCopy()
    {
        using var directory = new TemporaryDirectory();
        var segment = SealOneCommit(directory.Path);
        File.Delete(segment.Path);

        using var store = LocalDiskStore.Open(
            directory.Path,
            NewState(),
            remoteWalSegments: [new BytesRemoteSegment(segment.Id, segment.Bytes)]);

        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "wal"), "*.wal"));
        Assert.True(store.WriterEpoch > 0);
    }

    [Fact]
    public void ShouldReplayAliasedSegmentOnceWhenLocalAndRemoteMatch()
    {
        using var directory = new TemporaryDirectory();
        var segment = SealOneCommit(directory.Path);

        using var store = LocalDiskStore.Open(
            directory.Path,
            NewState(),
            remoteWalSegments: [new BytesRemoteSegment(segment.Id, segment.Bytes)]);

        Assert.True(File.Exists(segment.Path));
    }

    [Fact]
    public void ShouldRejectDivergedLocalAndRemoteAliasAsCorruptionEvenDuringSalvage()
    {
        using var directory = new TemporaryDirectory();
        var segment = SealOneCommit(directory.Path);
        var diverged = segment.Bytes.ToArray();
        diverged[^1] ^= 0xFF;

        Assert.Throws<PantsCorruptionException>(() => LocalDiskStore.Open(
            directory.Path,
            NewState(),
            recoveryPolicy: PantsRecoveryPolicy.Salvage,
            remoteWalSegments: [new BytesRemoteSegment(segment.Id, diverged)]));
    }

    static RuntimeState NewState() => new(
        new ManualClock(DateTimeOffset.UnixEpoch),
        new RuntimeTelemetry());

    static (string Path, ulong Id, byte[] Bytes) SealOneCommit(string root)
    {
        var state = NewState();
        using (var store = LocalDiskStore.Open(root, state))
        {
            _ = store.AppendCommit(CreateCommitPayload(state), state, PantsDurability.Sync);
            _ = store.RotateActiveLocalWal();
        }

        var path = Assert.Single(Directory.GetFiles(Path.Combine(root, "wal"), "*.wal"));
        return (path, ulong.Parse(Path.GetFileNameWithoutExtension(path), System.Globalization.CultureInfo.InvariantCulture), File.ReadAllBytes(path));
    }

    static CommitPayload CreateCommitPayload(RuntimeState state)
    {
        var operation = new TransactionIntentOperation(
            0,
            CommitOperationKind.Put,
            DefaultFamily,
            "remote-key"u8.ToArray(),
            null,
            "remote-value"u8.ToArray(),
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

    sealed class BytesRemoteSegment(ulong segmentId, byte[] bytes) : IRemoteWalSegment
    {
        public ulong SegmentId => segmentId;

        public string Name => $"remote-{segmentId}";

        public ulong SizeBytes => (ulong)bytes.Length;

        public Stream OpenRead() => new MemoryStream(bytes, false);
    }
}
