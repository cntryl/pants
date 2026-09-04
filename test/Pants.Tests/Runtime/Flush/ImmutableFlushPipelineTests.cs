using System.Collections.Concurrent;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime.Flush;

public sealed class ImmutableFlushPipelineTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShouldSerializeAttemptsInFrontierOrder()
    {
        var first = CreateFlush(30, 20, 1);
        var second = CreateFlush(20, 10, 1);
        var third = CreateFlush(10, 10, 1);
        var flushes = new List<ImmutableMemtableFlush> { first, second, third };
        var scheduled = new List<long>();
        var workers = flushes.ToDictionary(
            static flush => flush.Frozen.Id,
            static _ => new TaskCompletionSource<FrozenFlushRuntimeResult>(
                TaskCreationOptions.RunContinuationsAsynchronously));
        var completed = new ConcurrentDictionary<long, TaskCompletionSource>();
        foreach (var flush in flushes)
        {
            completed[flush.Frozen.Id] = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var pipeline = new ImmutableFlushPipeline(
            new RuntimeTelemetry(),
            (frozen, _) =>
            {
                scheduled.Add(frozen.Id);
                return ValueTask.FromResult(workers[frozen.Id].Task);
            },
            (flush, _, failure) =>
            {
                flush.CompleteAttempt(failure);
                flushes.Remove(flush);
                completed[flush.Frozen.Id].TrySetResult();
                return ValueTask.FromResult(false);
            },
            static (_, _) => ValueTask.CompletedTask,
            static () => false);

        await pipeline.ScheduleNextAsync(flushes, false);
        Assert.Equal([10], scheduled);

        await pipeline.ScheduleNextAsync(flushes, false);
        Assert.Equal([10], scheduled);

        workers[third.Frozen.Id].SetResult(SuccessfulResult());
        await completed[third.Frozen.Id].Task.WaitAsync(AssertionTimeout);
        await pipeline.ScheduleNextAsync(flushes, false);
        Assert.Equal([10, 20], scheduled);

        workers[second.Frozen.Id].SetResult(SuccessfulResult());
        await completed[second.Frozen.Id].Task.WaitAsync(AssertionTimeout);
        await pipeline.ScheduleNextAsync(flushes, false);
        Assert.Equal([10, 20, 30], scheduled);

        workers[first.Frozen.Id].SetResult(SuccessfulResult());
        await completed[first.Frozen.Id].Task.WaitAsync(AssertionTimeout);
        Assert.Empty(flushes);
    }

    [Fact]
    public async Task ShouldRetryFailedAttemptAfterBackoff()
    {
        var telemetry = new RuntimeTelemetry();
        var flush = CreateFlush(1, 1);
        var flushes = new List<ImmutableMemtableFlush> { flush };
        var succeeded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduledAttempts = 0;
        var pipeline = (ImmutableFlushPipeline?)null;
        pipeline = new ImmutableFlushPipeline(
            telemetry,
            (_, _) =>
            {
                var attempt = Interlocked.Increment(ref scheduledAttempts);
                return ValueTask.FromResult(
                    attempt == 1
                        ? Task.FromException<FrozenFlushRuntimeResult>(
                            new IOException("The first flush failed."))
                        : Task.FromResult(SuccessfulResult()));
            },
            (expected, _, failure) =>
            {
                expected.CompleteAttempt(failure);
                if (failure is null)
                {
                    flushes.Remove(expected);
                    succeeded.TrySetResult();
                }

                return ValueTask.FromResult(failure is not null);
            },
            (expected, _) => pipeline!.ScheduleNextAsync(
                flushes,
                expected.HasFailed),
            static () => false);
        var firstAttempt = flush.AttemptTask;

        await pipeline.ScheduleNextAsync(flushes, false);
        var firstFailure = await firstAttempt.WaitAsync(AssertionTimeout);
        await succeeded.Task.WaitAsync(AssertionTimeout);

        Assert.IsType<PantsIOException>(firstFailure);
        Assert.Equal(2, scheduledAttempts);
        Assert.Equal(2, flush.Attempts);
        Assert.Equal(1, telemetry.FlushRetriesTotal);
        Assert.False(flush.HasFailed);
        Assert.Empty(flushes);
    }

    [Fact]
    public async Task ShouldPropagateAttemptFailureWhileWaiting()
    {
        var expected = new PantsNoSpaceException("No space remains.");

        var actual = await Assert.ThrowsAsync<PantsNoSpaceException>(() =>
            ImmutableFlushPipeline.AwaitAttemptsAsync(
                    [Task.FromResult<Exception?>(null), Task.FromResult<Exception?>(expected)],
                    CancellationToken.None)
                .AsTask());

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task ShouldScheduleHealthyFamilyBehindFailedEarlierFamily()
    {
        var failed = CreateFlush(1, 1, 1);
        var healthy = CreateFlush(2, 2, 2);
        failed.BeginAttempt();
        failed.CompleteAttempt(new PantsIOException("Persistent family failure."));
        var scheduled = new List<long>();
        var worker = new TaskCompletionSource<FrozenFlushRuntimeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new ImmutableFlushPipeline(
            new RuntimeTelemetry(),
            (frozen, _) =>
            {
                scheduled.Add(frozen.Id);
                return ValueTask.FromResult(worker.Task);
            },
            static (_, _, _) => ValueTask.FromResult(false),
            static (_, _) => ValueTask.CompletedTask,
            static () => false);

        await pipeline.ScheduleNextAsync([failed, healthy], false);

        Assert.Equal([healthy.Frozen.Id], scheduled);
        Assert.True(failed.HasFailed);
        worker.SetResult(SuccessfulResult());
    }

    [Fact]
    public void ShouldGrowRetryBackoffExponentiallyAndClampAtOneSecond()
    {
        var delays = Enumerable.Range(1, 10)
            .Select(ImmutableFlushPipeline.GetRetryBackoff)
            .ToArray();

        Assert.Equal(
            [10, 20, 40, 80, 160, 320, 640, 1_000, 1_000, 1_000],
            delays.Select(static delay => delay.TotalMilliseconds));
    }

    [Fact]
    public async Task ShouldRetainGenerationAcrossFiveFailuresBeforeSingleSuccess()
    {
        var telemetry = new RuntimeTelemetry();
        var flush = CreateFlush(1, 1);
        var flushes = new List<ImmutableMemtableFlush> { flush };
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = (ImmutableFlushPipeline?)null;
        pipeline = new ImmutableFlushPipeline(
            telemetry,
            (_, _) => ValueTask.FromResult(
                flush.Attempts <= 5
                    ? Task.FromException<FrozenFlushRuntimeResult>(new IOException("Persistent failure."))
                    : Task.FromResult(SuccessfulResult())),
            (expected, _, failure) =>
            {
                expected.CompleteAttempt(failure);
                if (failure is not null)
                {
                    telemetry.RecordFlushFailure();
                }

                if (failure is null)
                {
                    flushes.Remove(expected);
                    completed.TrySetResult();
                }

                return ValueTask.FromResult(failure is not null);
            },
            (expected, _) => pipeline!.ScheduleNextAsync(flushes, expected.HasFailed),
            static () => false);

        await pipeline.ScheduleNextAsync(flushes, false);
        await completed.Task.WaitAsync(AssertionTimeout);

        Assert.Equal(6, flush.Attempts);
        Assert.Equal(5, telemetry.FlushFailuresTotal);
        Assert.Equal(5, telemetry.FlushRetriesTotal);
        Assert.False(flush.HasFailed);
        Assert.Empty(flushes);
    }

    [Fact]
    public void ShouldApplyImmutableQueueCapOnlyToSaturatedColumnFamily()
    {
        var telemetry = new RuntimeTelemetry();
        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            telemetry);
        var saturated = new ColumnFamilyIdentity(1, "family-1", 0);
        var healthy = new ColumnFamilyIdentity(2, "family-2", 0);
        for (var index = 0; index < MemtableWritePressure.MaximumImmutableMemtablesPerColumnFamily; index++)
        {
            var flush = CreateFlush(index + 1, checked((ulong)index + 1), saturated.Id);
            state.ImmutableMemtableFlushes.Add(flush.Frozen.Id, flush);
        }

        Assert.True(MemtableWritePressure.IsQueueFull(state, saturated));
        Assert.False(MemtableWritePressure.IsQueueFull(state, healthy));
    }

    static ImmutableMemtableFlush CreateFlush(long id, ulong frontierSequence)
        => CreateFlush(id, frontierSequence, checked((uint)id));

    static ImmutableMemtableFlush CreateFlush(long id, ulong frontierSequence, uint columnFamilyId)
    {
        var columnFamily = new ColumnFamilyIdentity(
            columnFamilyId,
            $"family-{columnFamilyId}",
            0);
        return new ImmutableMemtableFlush(new FrozenMemtableFlush(
            id,
            columnFamily,
            columnFamily.Id,
            [],
            checked((ulong)id),
            frontierSequence,
            1));
    }

    static FrozenFlushRuntimeResult SuccessfulResult() => new(
        null,
        false);
}
