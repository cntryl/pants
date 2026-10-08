namespace Cntryl.Pants.Storage.Internal;

/// <summary>
///     First-in, first-out exclusive access to the maintenance memory pool. Compaction rounds and
///     SST publications enter the gate before reserving pool memory, so neither ever observes the
///     other's reservations: a merge cannot fail because a publication is in flight, and a
///     whole-pool claimant is served in arrival order instead of polling behind a stream of smaller
///     reservations. A holder must never wait for the gate again while it holds it.
/// </summary>
sealed class MaintenanceMemoryGate
{
    readonly Lock _gate = new();
    readonly LinkedList<TaskCompletionSource<IDisposable>> _waiters = [];
    bool _held;

    public ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<IDisposable> waiter;
        LinkedListNode<TaskCompletionSource<IDisposable>> node;
        lock (_gate)
        {
            if (!_held)
            {
                _held = true;
                return ValueTask.FromResult<IDisposable>(new Hold(this));
            }

            waiter = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
            node = _waiters.AddLast(waiter);
        }

        return WaitAsync(waiter, node, cancellationToken);
    }

    async ValueTask<IDisposable> WaitAsync(
        TaskCompletionSource<IDisposable> waiter,
        LinkedListNode<TaskCompletionSource<IDisposable>> node,
        CancellationToken cancellationToken)
    {
        await using var registration = cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                if (node.List is null)
                {
                    // Already granted; the grant wins and the caller releases it normally.
                    return;
                }

                _waiters.Remove(node);
            }

            waiter.TrySetCanceled(cancellationToken);
        }).ConfigureAwait(false);
        return await waiter.Task.ConfigureAwait(false);
    }

    void Release()
    {
        TaskCompletionSource<IDisposable> next;
        lock (_gate)
        {
            if (_waiters.First is not { } first)
            {
                _held = false;
                return;
            }

            _waiters.RemoveFirst();
            next = first.Value;
        }

        next.SetResult(new Hold(this));
    }

    sealed class Hold(MaintenanceMemoryGate gate) : IDisposable
    {
        int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
