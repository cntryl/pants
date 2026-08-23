using System.Collections.Concurrent;

namespace Pants.Tests;

public sealed class ImmutableFlushPipelineTests
{
    static readonly TimeSpan AssertionTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ShouldSerializeAttemptsInFrontierOrder()
    {
        var first = CreateFlush(id: 30, frontierSequence: 20);
        var second = CreateFlush(id: 20, frontierSequence: 10);
        var third = CreateFlush(id: 10, frontierSequence: 10);
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

        await pipeline.ScheduleNextAsync(flushes, retryFailure: false);
        Assert.Equal([10], scheduled);

        await pipeline.ScheduleNextAsync(flushes, retryFailure: false);
        Assert.Equal([10], scheduled);

        workers[third.Frozen.Id].SetResult(SuccessfulResult());
        await completed[third.Frozen.Id].Task.WaitAsync(AssertionTimeout);
        await pipeline.ScheduleNextAsync(flushes, retryFailure: false);
        Assert.Equal([10, 20], scheduled);

        workers[second.Frozen.Id].SetResult(SuccessfulResult());
        await completed[second.Frozen.Id].Task.WaitAsync(AssertionTimeout);
        await pipeline.ScheduleNextAsync(flushes, retryFailure: false);
        Assert.Equal([10, 20, 30], scheduled);

        workers[first.Frozen.Id].SetResult(SuccessfulResult());
        await completed[first.Frozen.Id].Task.WaitAsync(AssertionTimeout);
        Assert.Empty(flushes);
    }

    [Fact]
    public async Task ShouldRetryFailedAttemptAfterBackoff()
    {
        var telemetry = new RuntimeTelemetry();
        var flush = CreateFlush(id: 1, frontierSequence: 1);
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
                retryFailure: expected.HasFailed),
            static () => false);
        var firstAttempt = flush.AttemptTask;

        await pipeline.ScheduleNextAsync(flushes, retryFailure: false);
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

    static ImmutableMemtableFlush CreateFlush(long id, ulong frontierSequence)
    {
        var columnFamily = new ColumnFamilyIdentity(
            checked((uint)id),
            $"family-{id}",
            Generation: 0);
        return new ImmutableMemtableFlush(new FrozenMemtableFlush(
            id,
            columnFamily,
            columnFamily.Id,
            Operations: [],
            SstSequence: checked((ulong)id),
            frontierSequence,
            SizeBytes: 1));
    }

    static FrozenFlushRuntimeResult SuccessfulResult() => new(
        PublicationPlan: null,
        PersistenceAnomaly: false);
}
