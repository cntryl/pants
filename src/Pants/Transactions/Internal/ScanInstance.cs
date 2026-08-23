namespace Cntryl.Pants.Transactions.Internal;

sealed class ScanInstance : IPantsScan, IAsyncEnumerator<PantsEntry>
{
    readonly ScanBounds _bounds;
    readonly Func<ValueTask> _releaseSnapshot;
    CancellationToken _cancellationToken;
    int _disposed;
    int _emitted;
    IEnumerator<PantsEntry>? _entries;
    Func<CancellationToken, ValueTask<IEnumerator<PantsEntry>>>? _entriesFactory;
    int _enumerationStarted;
    Exception? _failure;
    int _snapshotReleased;

    internal ScanInstance(
        Func<CancellationToken, ValueTask<IEnumerator<PantsEntry>>> entriesFactory,
        PantsScanQuery query,
        Func<ValueTask> releaseSnapshot)
    {
        _entriesFactory = entriesFactory ?? throw new ArgumentNullException(nameof(entriesFactory));
        _releaseSnapshot = releaseSnapshot ?? throw new ArgumentNullException(nameof(releaseSnapshot));
        _bounds = new ScanBounds(query);
        Direction = _bounds.Direction;
    }

    public PantsEntry Current { get; private set; }

    public ValueTask<bool> MoveNextAsync()
    {
        if (_failure is { } failure)
        {
            return ValueTask.FromException<bool>(failure);
        }

        if (State != PantsIteratorState.Active || Volatile.Read(ref _disposed) != 0)
        {
            return ValueTask.FromResult(false);
        }

        return MoveNextCoreAsync();
    }

    public PantsScanDirection Direction { get; }

    public PantsIteratorState State { get; private set; } = PantsIteratorState.Active;

    public bool IsActive => State == PantsIteratorState.Active;

    public bool IsExhausted => State == PantsIteratorState.Exhausted;

    public bool IsFailed => State == PantsIteratorState.Failed;

    public IAsyncEnumerator<PantsEntry> GetAsyncEnumerator(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _enumerationStarted, 1) != 0)
        {
            Exception error = PantsException.Create(
                PantsErrorCode.Busy,
                "A Pants scan is a single-pass cursor and has already been enumerated.");
            Fail(error);
            throw error;
        }

        _cancellationToken = cancellationToken;
        return this;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && State == PantsIteratorState.Active)
        {
            State = PantsIteratorState.Exhausted;
            Current = default;
            _entries?.Dispose();
            _entries = null;
        }

        await ReleaseSnapshotAsync().ConfigureAwait(false);
    }

    async ValueTask<bool> MoveNextCoreAsync()
    {
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_emitted >= _bounds.Limit)
            {
                await ExhaustAsync().ConfigureAwait(false);
                return false;
            }

            await EnsureEntriesInitializedAsync().ConfigureAwait(false);
            while (_entries!.MoveNext())
            {
                var candidate = _entries.Current;
                var key = candidate.Key.Span;
                if (!_bounds.Matches(key))
                {
                    continue;
                }

                Current = new PantsEntry(candidate.Key.ToArray(), candidate.Value.ToArray());
                _emitted++;
                return true;
            }

            await ExhaustAsync().ConfigureAwait(false);
            return false;
        }
        catch (Exception exception)
        {
            Fail(exception);
            try
            {
                await ReleaseSnapshotAsync().ConfigureAwait(false);
            }
            catch
            {
                // Preserve the first terminal failure.
            }

            throw;
        }
    }

    async ValueTask EnsureEntriesInitializedAsync()
    {
        if (_entries is not null)
        {
            return;
        }

        var factory = _entriesFactory ??
                      throw new PantsInternalException("The scan entry source is unavailable.");
        _entries = await factory(_cancellationToken).ConfigureAwait(false);
        _entriesFactory = null;
    }

    async ValueTask ExhaustAsync()
    {
        State = PantsIteratorState.Exhausted;
        Current = default;
        _entries?.Dispose();
        _entries = null;
        await ReleaseSnapshotAsync().ConfigureAwait(false);
    }

    void Fail(Exception exception)
    {
        _failure ??= exception;
        State = PantsIteratorState.Failed;
        Current = default;
        _entries?.Dispose();
        _entries = null;
    }

    ValueTask ReleaseSnapshotAsync() =>
        Interlocked.Exchange(ref _snapshotReleased, 1) == 0
            ? _releaseSnapshot()
            : ValueTask.CompletedTask;
}
