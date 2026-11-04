using System.Collections.Concurrent;
using Cntryl.Pants.Recovery;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Observability;

public sealed class PantsRecoveryRemoteWorkTests
{
    [Fact]
    public async Task ShouldReportVerifiedCatalogWalBytesAndFramesWhileRecoveringTheExactRemoteValue()
    {
        using var directory = new TemporaryDirectory();
        var cloud = new ScriptedCloudObjectStore();
        var leaseStore = new TestCloudLeaseStore();
        using var lease = new CloudLeaseCoordinator(leaseStore, new ManualClock(DateTimeOffset.UnixEpoch),
            "writer", TimeSpan.FromSeconds(10), TimeSpan.Zero);
        var epoch = await lease.AcquireAsync(CancellationToken.None);
        await using var publisher = new ProviderCloudPersistence(directory.Path, cloud,
            new ScriptedCloudObjectStore(), new ScriptedCloudObjectStore(), lease);
        var expected = new byte[96 * 1024];
        new Random(229).NextBytes(expected);
        var segment = TestWalSegment.Create(1, epoch, 1, "remote.wal", expected);
        await publisher.PublishWalBatchAsync([segment], CancellationToken.None);
        var completed = new List<RecoveryWorkCompleted>();
        using var sink = new RecoveryWorkSink(completed.Add);

        var hydration = await ProviderCloudPersistence.HydrateLocalCacheAsync(directory.Path,
            cloud, new ScriptedCloudObjectStore(), new ScriptedCloudObjectStore(), PantsRecoveryPolicy.Strict,
            static _ => ValueTask.CompletedTask, CancellationToken.None, recoveryWork: sink);
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        using (var store = LocalDiskStore.Open(directory.Path, state,
                   minimumWriterEpoch: epoch, remoteWalSegments: hydration.RemoteWalSegments,
                   remoteNextWalSegmentId: hydration.NextWalSegmentId, remoteWalHead: hydration.WalHead,
                   remoteSequenceFloor: hydration.SequenceFloor, recoveryWork: sink))
        {
            Assert.Equal(expected, state.FamilyData.Single().Value["key"u8.ToArray()].Value);
            Assert.Equal(1UL, state.Sequence);
            Assert.Single(Directory.EnumerateFiles(Path.Combine(directory.Path, "wal"), "*.wal"));
            Assert.Equal(segment.Bytes.Length, completed.Where(static work => work.Stage == "crc_validation").Sum(static work => work.Bytes));
            Assert.True(completed.Where(static work => work.Stage == "remote_read").Sum(static work => work.Bytes) >= segment.Bytes.Length);
            Assert.Equal(1, completed.Where(static work => work.Stage == "wal_replay").Sum(static work => work.Frames));
            store.Flush(state);
        }

        await using var recovered = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        await using var reader = await recovered.Transactions.BeginAsync(
            recovered.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal(expected, (await reader.GetAsync("key"u8.ToArray()))?.ToArray());
    }

    [Theory]
    [InlineData("success")]
    [InlineData("error")]
    [InlineData("empty")]
    public async Task ShouldReportRemoteWorkOnlyAfterAnExactNonemptyRangeCompletes(string outcome)
    {
        var store = new ScriptedCloudObjectStore { ShortenRangeByBytes = outcome == "empty" ? 8 : 0 };
        store.Write("object", "expected"u8.ToArray());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeRangeRead = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            if (outcome == "error")
            {
                throw new IOException("The held range failed.");
            }
        };
        var completed = new ConcurrentQueue<RecoveryWorkCompleted>();
        using var sink = new RecoveryWorkSink(completed.Enqueue);
        using var stream = new RemoteRangeStream(store, "object", 8, 8, CancellationToken.None, sink);
        var read = Task.Run(() =>
        {
            var bytes = new byte[8];
            DiskFormat.ReadExactly(stream, bytes);
            return bytes;
        });
        try
        {
            await entered.Task.WaitAsync(TestTimeouts.Expected);
            Assert.Empty(completed);
        }
        finally
        {
            release.TrySetResult();
        }

        if (outcome == "success")
        {
            Assert.Equal("expected"u8.ToArray(), await read.WaitAsync(TestTimeouts.Expected));
            var work = Assert.Single(completed);
            Assert.Equal("remote_read", work.Stage);
            Assert.Equal(8, work.Bytes);
        }
        else
        {
            await Assert.ThrowsAnyAsync<Exception>(() => read.WaitAsync(TestTimeouts.Expected));
            Assert.Empty(completed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldIgnoreLateActualRecoveryWorkAfterConsumerScopeOrPhaseEnds(bool changePhase)
    {
        using var directory = new TemporaryDirectory();
        var expected = new byte[96 * 1024];
        new Random(227).NextBytes(expected);
        var sealedSegment = TestWalSegment.Create(1, 7, 1, "remote.wal", expected);
        var publication = TestWalSegment.Publish(sealedSegment);
        var cloud = new ScriptedCloudObjectStore();
        cloud.Write(publication.ObjectKey, sealedSegment.Bytes);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        cloud.BeforeRangeRead = async (_, _) =>
        {
            if (Interlocked.Exchange(ref first, 1) == 0)
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        var time = new ManualTimeProvider();
        var tracker = new RecoveryProgressTracker(time);
        using var scope = tracker.EnterRecovery();
        var produced = new ConcurrentQueue<RecoveryWorkCompleted>();
        using var sink = new RecoveryWorkSink(work =>
        {
            produced.Enqueue(work);
            scope.Record(work);
        });
        var remote = new ProviderRemoteWalSegment(cloud, publication, 64 * 1024, sink);
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        var recovery = Task.Run(() => LocalDiskStore.Open(directory.Path, state,
            minimumWriterEpoch: 7, remoteWalSegments: [remote],
            remoteWalHead: sealedSegment.Facts.Link, remoteSequenceFloor: 1, recoveryWork: sink));
        long credited;
        try
        {
            await entered.Task.WaitAsync(TestTimeouts.Expected);
            credited = tracker.CompletedEvents;
            Assert.DoesNotContain(produced, static work => work.Stage == "remote_read");
            if (changePhase)
            {
                tracker.AdvancePhase();
            }
            else
            {
                scope.Dispose();
            }
            time.Advance(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.TrySetResult();
        }

        using (var store = await recovery.WaitAsync(TestTimeouts.Expected))
        {
            Assert.Equal(expected, state.FamilyData.Single().Value["key"u8.ToArray()].Value);
            Assert.Equal(1UL, state.Sequence);
            Assert.Contains(produced, static work => work.Stage == "remote_read" && work.Bytes > 0);
            Assert.Contains(produced, static work => work.Stage == "wal_replay" && work.Frames > 0);
            Assert.Equal(credited, tracker.CompletedEvents);
            Assert.True(scope.IdleFor >= TimeSpan.FromSeconds(5));
            sink.Dispose();
            store.Flush(state);
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal(expected, (await reader.GetAsync("key"u8.ToArray()))?.ToArray());
    }
}
