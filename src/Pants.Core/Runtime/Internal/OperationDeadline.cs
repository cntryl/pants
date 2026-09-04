namespace Cntryl.Pants.Runtime.Internal;

/// <summary>
///     One monotonic budget shared across a sequence of nested runtime and storage operations.
/// </summary>
readonly struct OperationDeadline
{
    readonly TimeSpan _budget;
    readonly long _started;
    readonly TimeProvider _timeProvider;

    OperationDeadline(
        long started,
        TimeSpan budget,
        TimeProvider timeProvider,
        bool bounded)
    {
        _started = started;
        _budget = budget;
        _timeProvider = timeProvider;
        IsBounded = bounded;
    }

    public static OperationDeadline Unbounded { get; } = new(
        0,
        TimeSpan.MaxValue,
        TimeProvider.System,
        false);

    public static OperationDeadline FromBudget(
        TimeSpan budget,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, TimeSpan.Zero);

        var provider = timeProvider ?? TimeProvider.System;
        return new OperationDeadline(provider.GetTimestamp(), budget, provider, true);
    }

    public static OperationDeadline FromStart(
        long started,
        TimeSpan budget,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, TimeSpan.Zero);

        return new OperationDeadline(
            started,
            budget,
            timeProvider ?? TimeProvider.System,
            true);
    }

    public bool IsBounded { get; }

    public bool IsExpired => IsBounded && Remaining == TimeSpan.Zero;

    public TimeSpan Remaining
    {
        get
        {
            if (!IsBounded)
            {
                return TimeSpan.MaxValue;
            }

            var elapsed = _timeProvider.GetElapsedTime(_started);
            return elapsed >= _budget ? TimeSpan.Zero : _budget - elapsed;
        }
    }

    public TimeSpan Clamp(TimeSpan perOperationTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perOperationTimeout, TimeSpan.Zero);

        return IsBounded
            ? TimeSpan.FromTicks(Math.Min(perOperationTimeout.Ticks, Remaining.Ticks))
            : perOperationTimeout;
    }

    public ValueTask RunAsync(
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken) =>
        RunCoreAsync(operation, false, cancellationToken);

    public ValueTask RunMutationAsync(
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken) =>
        RunCoreAsync(operation, true, cancellationToken);

    public ValueTask<T> RunAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken) =>
        RunCoreAsync(operation, false, cancellationToken);

    public ValueTask<T> RunMutationAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken) =>
        RunCoreAsync(operation, true, cancellationToken);

    async ValueTask RunCoreAsync(
        Func<CancellationToken, ValueTask> operation,
        bool mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsBounded)
        {
            await operation(cancellationToken).ConfigureAwait(false);
            return;
        }

        var remaining = Remaining;
        ThrowIfExpired(remaining);
        using var timeout = new CancellationTokenSource(remaining, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            await operation(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            ThrowExpired(exception, mutation);
        }
    }

    async ValueTask<T> RunCoreAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        bool mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsBounded)
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        var remaining = Remaining;
        ThrowIfExpired(remaining);
        using var timeout = new CancellationTokenSource(remaining, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            return await operation(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            ThrowExpired(exception, mutation);
            throw;
        }
    }

    static void ThrowIfExpired(TimeSpan remaining)
    {
        if (remaining == TimeSpan.Zero)
        {
            throw new PantsTimeoutException(
                "Operation deadline expired before submission; no storage request was issued.");
        }
    }

    static void ThrowExpired(OperationCanceledException exception, bool mutation)
    {
        if (mutation)
        {
            throw new PantsIOException(
                "Cloud mutation outcome is indeterminate after the shared operation deadline expired.",
                exception);
        }

        throw new PantsTimeoutException(
            "Cloud operation exceeded its shared operation deadline.",
            exception);
    }
}
