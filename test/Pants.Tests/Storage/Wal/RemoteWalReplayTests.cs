using Cntryl.Pants.Runtime.Internal;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

public sealed class RemoteWalReplayTests
{
    [Theory]
    [InlineData(PantsRecoveryPolicy.Strict, "timeout")]
    [InlineData(PantsRecoveryPolicy.Salvage, "timeout")]
    [InlineData(PantsRecoveryPolicy.Strict, "busy")]
    [InlineData(PantsRecoveryPolicy.Salvage, "busy")]
    [InlineData(PantsRecoveryPolicy.Strict, "io")]
    [InlineData(PantsRecoveryPolicy.Salvage, "io")]
    public void ShouldPreserveTypedRemoteFailureWithoutChangingLocalHistory(PantsRecoveryPolicy policy, string kind)
    {
        using var source = new TemporaryDirectory();
        var segment = SealOneCommit(source.Path);
        using var consumer = new TemporaryDirectory();
        var state = NewState();
        PantsException expected = kind switch
        {
            "timeout" => new PantsTimeoutException("Injected remote verification timeout."),
            "busy" => new PantsBusyException("Injected remote verification contention."),
            _ => new PantsIOException("Injected remote verification I/O failure.")
        };
        using var store = LocalDiskStore.Open(consumer.Path, state, recoveryPolicy: policy,
            remoteWalSegments: [new VerificationFailureSegment(segment.Id, segment.Bytes, expected)],
            remoteWalHead: segment.Facts.Link, remoteSequenceFloor: segment.Facts.Link.LastSeq, deferWalReplay: true);
        var ledgerPath = Path.Combine(consumer.Path, "wal", "LEDGER");
        var authority = File.ReadAllBytes(ledgerPath);

        var actual = Record.Exception(() => store.ReplayDeferredWal(state, new StartupPhaseRecorder(null), static _ => { }));

        Assert.Same(expected, actual);
        Assert.Equal(authority, File.ReadAllBytes(ledgerPath));
    }

    static readonly ColumnFamilyIdentity DefaultFamily = new(
        0,
        "default",
        RuntimeState.DefaultFamilyVersion);

    [Fact]
    public void ShouldRecoverCommitFromRemoteOnlySegmentWithoutALocalCopy()
    {
        using var directory = new TemporaryDirectory();
        var segment = SealOneCommit(directory.Path);
        using var consumer = new TemporaryDirectory();

        using var store = LocalDiskStore.Open(
            consumer.Path,
            NewState(),
            remoteWalSegments: [new BytesRemoteSegment(segment.Id, segment.Bytes)],
            remoteWalHead: segment.Facts.Link,
            remoteSequenceFloor: segment.Facts.Link.LastSeq);

        Assert.Single(Directory.GetFiles(Path.Combine(consumer.Path, "wal"), "*.wal"));
        Assert.True(store.WriterEpoch > 0);
    }

    [Fact]
    public async Task ShouldHonorStartupDeadlineDuringDeferredRemoteWalReplay()
    {
        using var directory = new TemporaryDirectory();
        var segment = SealOneCommit(directory.Path);
        using var consumer = new TemporaryDirectory();
        var remote = new BlockingBytesRemoteSegment(segment.Id, segment.Bytes);
        var state = NewState();
        using var store = LocalDiskStore.Open(
            consumer.Path,
            state,
            remoteWalSegments: [remote],
            deferWalReplay: true,
            remoteWalHead: segment.Facts.Link,
            remoteSequenceFloor: segment.Facts.Link.LastSeq);
        var deadline = OperationDeadline.FromBudget(TimeSpan.FromSeconds(1));
        var replay = Task.Run(async () => await deadline.RunAsync(
            token =>
            {
                store.ReplayDeferredWal(
                    state,
                    new StartupPhaseRecorder(null),
                    static _ => { },
                    token);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None));

        await remote.ReadStarted.WaitAsync(TestTimeouts.Expected);

        await Assert.ThrowsAsync<PantsTimeoutException>(() =>
            replay.WaitAsync(TestTimeouts.Expected));
        Assert.True(remote.ReadToken.CanBeCanceled);
        Assert.True(remote.ReadToken.IsCancellationRequested);
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

    static (string Path, ulong Id, byte[] Bytes, WalSealedFacts Facts) SealOneCommit(string root)
    {
        var state = NewState();
        using (var store = LocalDiskStore.Open(root, state))
        {
            _ = store.AppendCommit(CreateCommitPayload(state), state, PantsDurability.Sync);
            _ = store.RotateActiveLocalWal();
        }

        var ledger = WalLedgerStore.LoadExisting(Path.Combine(root, "wal"));
        var member = Assert.Single(ledger.Members, static member => member.Sealed is not null);
        var path = Path.Combine(root, "wal", $"{member.Open.Id:00000000000000000000}.wal");
        return (path, ulong.Parse(Path.GetFileNameWithoutExtension(path), System.Globalization.CultureInfo.InvariantCulture), File.ReadAllBytes(path), member.Sealed!);
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

        public Stream OpenRead(CancellationToken cancellationToken = default) =>
            new MemoryStream(bytes, false);
    }

    sealed class VerificationFailureSegment(ulong segmentId, byte[] bytes, PantsException failure) : IRemoteWalSegment
    {
        public ulong SegmentId => segmentId;
        public string Name => $"remote-{segmentId}";
        public ulong SizeBytes => (ulong)bytes.Length;
        public Stream OpenRead(CancellationToken cancellationToken = default) => new VerificationFailureStream(bytes, failure);
    }

    sealed class VerificationFailureStream(byte[] bytes, PantsException failure) : MemoryStream(bytes, false)
    {
        public override long Position
        {
            get => base.Position;
            set
            {
                if (value == 0 && base.Position == Length)
                {
                    throw failure;
                }

                base.Position = value;
            }
        }
    }

    sealed class BlockingBytesRemoteSegment(ulong segmentId, byte[] bytes) : IRemoteWalSegment
    {
        readonly TaskCompletionSource _readStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ulong SegmentId => segmentId;

        public string Name => $"remote-{segmentId}";

        public ulong SizeBytes => (ulong)bytes.Length;

        public Task ReadStarted => _readStarted.Task;

        public CancellationToken ReadToken { get; private set; }

        public Stream OpenRead(CancellationToken cancellationToken = default)
        {
            ReadToken = cancellationToken;
            return new CancellationBlockingStream(bytes, _readStarted, cancellationToken);
        }
    }

    sealed class CancellationBlockingStream(
        byte[] bytes,
        TaskCompletionSource readStarted,
        CancellationToken cancellationToken) : MemoryStream(bytes, false)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            WaitForCancellation();
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            WaitForCancellation();
            return base.Read(buffer);
        }

        void WaitForCancellation()
        {
            readStarted.TrySetResult();
            if (!cancellationToken.WaitHandle.WaitOne(TestTimeouts.Expected))
            {
                throw new TimeoutException("The startup deadline did not reach remote WAL replay.");
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
