namespace Cntryl.Pants.Runtime.Internal.Services.Flush;

sealed class ImmutableFlushPipeline
{
    static readonly TimeSpan InitialRetryBackoff = TimeSpan.FromMilliseconds(10);
    static readonly TimeSpan MaximumRetryBackoff = TimeSpan.FromSeconds(1);

    readonly Func<
        ImmutableMemtableFlush,
        FrozenFlushRuntimeResult?,
        Exception?,
        ValueTask<bool>> _completeAttemptAsync;

    readonly Func<bool> _isDisposed;
    readonly Func<ImmutableMemtableFlush, int, ValueTask> _retryAttemptAsync;

    readonly Func<
        FrozenMemtableFlush,
        FlushPublicationPlan?,
        ValueTask<Task<FrozenFlushRuntimeResult>>> _scheduleWorkerAsync;

    readonly RuntimeTelemetry _telemetry;
    readonly TimeProvider _timeProvider;

    public ImmutableFlushPipeline(
        RuntimeTelemetry telemetry,
        Func<
            FrozenMemtableFlush,
            FlushPublicationPlan?,
            ValueTask<Task<FrozenFlushRuntimeResult>>> scheduleWorkerAsync,
        Func<
            ImmutableMemtableFlush,
            FrozenFlushRuntimeResult?,
            Exception?,
            ValueTask<bool>> completeAttemptAsync,
        Func<ImmutableMemtableFlush, int, ValueTask> retryAttemptAsync,
        Func<bool> isDisposed,
        TimeProvider? timeProvider = null)
    {
        _telemetry = telemetry;
        _scheduleWorkerAsync = scheduleWorkerAsync;
        _completeAttemptAsync = completeAttemptAsync;
        _retryAttemptAsync = retryAttemptAsync;
        _isDisposed = isDisposed;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask ScheduleNextAsync(
        IReadOnlyCollection<ImmutableMemtableFlush> flushes,
        bool retryFailure)
    {
        var next = flushes
            .OrderBy(static flush => flush.Frozen.FrontierSequence)
            .ThenBy(static flush => flush.Frozen.Id)
            .ThenBy(static flush => flush.Frozen.ColumnFamilyId)
            .GroupBy(static flush => flush.Frozen.ColumnFamily)
            .Select(static family => family.First())
            .FirstOrDefault(flush => !flush.IsRunning && (!flush.HasFailed || retryFailure));
        if (next is null)
        {
            return;
        }

        await ScheduleAttemptAsync(next).ConfigureAwait(false);
    }

    public static async ValueTask AwaitAttemptsAsync(
        IReadOnlyList<Task<Exception?>> attempts,
        CancellationToken cancellationToken)
    {
        foreach (var attempt in attempts)
        {
            var failure = await attempt.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                throw failure;
            }
        }
    }

    public static async ValueTask AwaitWorkersAsync(
        IReadOnlyCollection<ImmutableMemtableFlush> flushes,
        CancellationToken cancellationToken)
    {
        var workers = flushes
            .Select(static flush => flush.RunningTask)
            .Where(static task => task is not null)
            .Cast<Task>()
            .ToArray();
        foreach (var worker in workers)
        {
            try
            {
                await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The operation waiter owns the result. This waits only for quiescence.
            }
        }
    }

    async ValueTask ScheduleAttemptAsync(ImmutableMemtableFlush flush)
    {
        if (flush.Attempts > 0)
        {
            _telemetry.RecordFlushRetry();
        }

        flush.BeginAttempt();
        var workerTask = await _scheduleWorkerAsync(
                flush.Frozen,
                flush.PublicationPlan)
            .ConfigureAwait(false);
        flush.AttachRunningTask(workerTask);
        _ = ObserveAttemptAsync(flush, workerTask);
    }

    async Task ObserveAttemptAsync(
        ImmutableMemtableFlush expected,
        Task<FrozenFlushRuntimeResult> workerTask)
    {
        var result = (FrozenFlushRuntimeResult?)null;
        var failure = (Exception?)null;
        try
        {
            result = await workerTask.ConfigureAwait(false);
        }
        catch (FrozenFlushRuntimeException exception)
        {
            result = exception.Result;
            failure = RuntimeExceptionMapper.ToPublicException(
                exception.InnerException ?? exception);
        }
        catch (Exception exception)
        {
            failure = RuntimeExceptionMapper.ToPublicException(exception);
        }

        try
        {
            var shouldRetry = await _completeAttemptAsync(expected, result, failure)
                .ConfigureAwait(false);
            if (failure is not null && shouldRetry)
            {
                _ = RetryAfterDelayAsync(
                    expected,
                    expected.Attempts,
                    GetRetryBackoff(expected.Attempts));
            }
        }
        catch (Exception exception)
        {
            expected.CompleteAttempt(RuntimeExceptionMapper.ToPublicException(exception));
        }
    }

    async Task RetryAfterDelayAsync(
        ImmutableMemtableFlush expected,
        int failedAttempt,
        TimeSpan delay)
    {
        await Task.Delay(delay, _timeProvider, CancellationToken.None).ConfigureAwait(false);
        if (_isDisposed())
        {
            return;
        }

        try
        {
            await _retryAttemptAsync(expected, failedAttempt).ConfigureAwait(false);
        }
        catch (PantsAbortedException)
        {
        }
    }

    public static TimeSpan GetRetryBackoff(int attempts)
    {
        var exponent = Math.Min(Math.Max(attempts - 1, 0), 7);
        var milliseconds = Math.Min(
            checked((long)InitialRetryBackoff.TotalMilliseconds << exponent),
            checked((long)MaximumRetryBackoff.TotalMilliseconds));
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}
