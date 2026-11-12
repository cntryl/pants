using Cntryl.Pants.Checkpoint;
using Cntryl.Pants.Storage.Internal.Accounting;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Performance.Checkpoint;

public sealed class CheckpointBoundaryTests
{
    [Fact]
    public async Task ShouldExcludeWarmupWaitButIncludeAcceptedQueryAndWholeEndCapture()
    {
        var time = new ManualTimeProvider();
        var owner = new MetadataAccountingOwner(time);
        var idle = owner.Handle.Snapshot();
        var busy = idle with { Buckets = idle.Buckets.SetItem(0, idle.Buckets[0] with { ActiveOperations = 1 }) };
        var calls = 0;
        var observation = new CheckpointBoundary.Observation();
        var deadline = CheckpointClock.Deadline(time, 0, TimeSpan.FromSeconds(1));

        var before = await CheckpointBoundary.CaptureAsync(time, deadline, TimeSpan.FromSeconds(30), observation,
            _ =>
            {
                time.Advance(TimeSpan.FromMilliseconds(3));
                return ValueTask.FromResult((new PantsRuntimeMetrics(), ++calls == 1 ? busy : idle));
            }, delay => { time.Advance(delay); return ValueTask.CompletedTask; });

        Assert.Equal(TimeSpan.FromMilliseconds(4).Ticks, before.QueryStarted);
        Assert.Equal(TimeSpan.FromMilliseconds(3), time.GetElapsedTime(before.QueryStarted));
        Assert.Equal(2UL, observation.Samples);
        Assert.Equal(1UL, observation.PauseCalls);
        Assert.False(observation.ProgressAdvanced);
        await CheckpointBoundary.CaptureAsync(time, deadline, TimeSpan.FromSeconds(30), new CheckpointBoundary.Observation(),
            _ => { time.Advance(TimeSpan.FromMilliseconds(2)); return ValueTask.FromResult((new PantsRuntimeMetrics(), idle)); });
        Assert.Equal(TimeSpan.FromMilliseconds(5), time.GetElapsedTime(before.QueryStarted));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(60, 30)]
    public async Task ShouldRejectIdleCandidateAtOriginalCellOrCappedBoundaryDeadline(int cellSeconds, int expectedSeconds)
    {
        var time = new ManualTimeProvider();
        var idle = new MetadataAccountingOwner().Handle.Snapshot();
        var observation = new CheckpointBoundary.Observation();
        var calls = 0;

        await Assert.ThrowsAsync<PantsTimeoutException>(() => CheckpointBoundary.CaptureAsync(time,
            CheckpointClock.Deadline(time, 0, TimeSpan.FromSeconds(cellSeconds)), TimeSpan.FromMinutes(1), observation,
            remaining =>
            {
                calls++;
                Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), remaining);
                time.Advance(remaining);
                return ValueTask.FromResult((new PantsRuntimeMetrics(), idle));
            }).AsTask());

        Assert.Equal(1, calls);
        Assert.False(observation.Complete);
    }

    [Fact]
    public async Task ShouldKeepOriginalBoundaryAllowanceAcrossBusyCandidatesAndPauses()
    {
        var time = new ManualTimeProvider();
        var snapshot = new MetadataAccountingOwner().Handle.Snapshot();
        snapshot = snapshot with { Buckets = snapshot.Buckets.SetItem(0, snapshot.Buckets[0] with { ActiveOperations = 1 }) };
        var calls = 0;
        var observation = new CheckpointBoundary.Observation();

        await Assert.ThrowsAsync<PantsTimeoutException>(() => CheckpointBoundary.CaptureAsync(time,
            CheckpointClock.Deadline(time, 0, TimeSpan.FromMilliseconds(50)), TimeSpan.FromSeconds(30), observation,
            _ =>
            {
                time.Advance(TimeSpan.FromMilliseconds(++calls == 1 ? 30 : 20));
                return ValueTask.FromResult((new PantsRuntimeMetrics(), snapshot));
            }, delay => { time.Advance(delay); return ValueTask.CompletedTask; }).AsTask());

        Assert.Equal(2, calls);
        Assert.Equal(1UL, observation.PauseCalls);
        Assert.False(observation.Complete);
    }

    [Fact]
    public async Task ShouldSampleOnlyPersistentMetadataRatherThanRequireGlobalRuntimeIdleness()
    {
        var time = new ManualTimeProvider();
        var snapshot = new MetadataAccountingOwner().Handle.Snapshot();
        snapshot = snapshot with { Buckets = snapshot.Buckets.SetItem(1, snapshot.Buckets[1] with { ActiveOperations = 7 }) };

        var capture = await CheckpointBoundary.CaptureAsync(time,
            CheckpointClock.Deadline(time, 0, TimeSpan.FromSeconds(1)), TimeSpan.FromSeconds(30), new CheckpointBoundary.Observation(),
            _ => ValueTask.FromResult((new PantsRuntimeMetrics { FlushInFlight = 1, ActiveCompactions = 1 }, snapshot)));

        Assert.Equal(1, capture.Runtime.FlushInFlight);
        Assert.Equal(7UL, capture.Snapshot.Buckets[1].ActiveOperations);
    }

    [Fact]
    public async Task ShouldPreserveTypedQueryFailureWithoutAdmittingAnotherSample()
    {
        var expected = new PantsBusyException("controlled owner query failed");
        var time = new ManualTimeProvider();
        var observation = new CheckpointBoundary.Observation();

        var actual = await Assert.ThrowsAsync<PantsBusyException>(() => CheckpointBoundary.CaptureAsync(time,
            CheckpointClock.Deadline(time, 0, TimeSpan.FromSeconds(1)), TimeSpan.FromSeconds(30), observation,
            _ => throw expected).AsTask());

        Assert.Same(expected, actual);
        Assert.Equal(1UL, observation.Samples);
    }

    [Fact]
    public async Task ShouldNotTreatSuccessfulStallWaitAsCommitOrRefreshOriginalAllowance()
    {
        var time = new ManualTimeProvider();
        var calls = 0;
        var observation = new CheckpointCommitPolicy.Observation();

        await Assert.ThrowsAsync<PantsTimeoutException>(() => CheckpointCommitPolicy.CommitAsync(time,
            CheckpointClock.Deadline(time, 0, TimeSpan.FromMilliseconds(10)), observation,
            () => { calls++; throw new PantsWriteStallException("rejected before admission"); },
            remaining => { time.Advance(remaining); return ValueTask.FromResult(true); }).AsTask());

        Assert.Equal(1, calls);
        Assert.Equal(0UL, observation.SuccessfulCommits);
        Assert.Equal(1UL, observation.WaitCalls);
    }

    [Fact]
    public async Task ShouldNeverRetryAdmittedOutcomeUnknown()
    {
        var time = new ManualTimeProvider();
        var expected = new PantsCommitOutcomeUnknownException("accepted uncertain commit");
        var observation = new CheckpointCommitPolicy.Observation();

        var actual = await Assert.ThrowsAsync<PantsCommitOutcomeUnknownException>(() => CheckpointCommitPolicy.CommitAsync(time,
            CheckpointClock.Deadline(time, 0, TimeSpan.FromSeconds(1)), observation,
            () => throw expected, _ => throw new InvalidOperationException("must not wait or retry")).AsTask());

        Assert.Same(expected, actual);
        Assert.Equal(1UL, observation.Attempts);
        Assert.Equal(0UL, observation.WaitCalls);
    }
}
