namespace Cntryl.Pants.Transactions.Internal;

sealed class ScanInstance : IPantsScan, IAsyncEnumerator<PantsEntry>
{
    readonly ScanBounds _bounds;
    readonly Func<ValueTask> _releaseSnapshot;
    CancellationToken _cancellationToken;
    int _disposed;
    int _emitted;
    IAsyncEnumerator<PantsEntry>? _entries;
    Func<CancellationToken, ValueTask<IAsyncEnumerator<PantsEntry>>>? _entriesFactory;
    int _enumerationStarted;
    Exception? _failure;
    int _snapshotReleased;

    internal ScanInstance(
        Func<CancellationToken, ValueTask<IAsyncEnumerator<PantsEntry>>> entriesFactory,
        PantsScanQuery query,
        Func<ValueTask> releaseSnapshot)
    {
        _entriesFactory = entriesFactory ?? throw new ArgumentNullException(nameof(entriesFactory));
        _releaseSnapshot = releaseSnapshot ?? throw new ArgumentNullException(nameof(releaseSnapshot));
        _bounds = new ScanBounds(query);
        Direction = _bounds.Direction;
    }

    internal ScanInstance(
        Func<CancellationToken, ValueTask<IEnumerator<PantsEntry>>> entriesFactory,
        PantsScanQuery query,
        Func<ValueTask> releaseSnapshot)
        : this(
            async cancellationToken => new SyncAsyncEnumerator(
                await entriesFactory(cancellationToken).ConfigureAwait(false)),
            query,
            releaseSnapshot)
    {
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
            FailAsync(error).AsTask().GetAwaiter().GetResult();
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
            if (_entries is not null)
            {
                await _entries.DisposeAsync().ConfigureAwait(false);
            }

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
            while (await _entries!.MoveNextAsync().ConfigureAwait(false))
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
            await FailAsync(exception).ConfigureAwait(false);
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
        if (_entries is not null)
        {
            await _entries.DisposeAsync().ConfigureAwait(false);
        }

        _entries = null;
        await ReleaseSnapshotAsync().ConfigureAwait(false);
    }

    async ValueTask FailAsync(Exception exception)
    {
        _failure ??= exception;
        State = PantsIteratorState.Failed;
        Current = default;
        if (_entries is not null)
        {
            await _entries.DisposeAsync().ConfigureAwait(false);
        }

        _entries = null;
    }

    ValueTask ReleaseSnapshotAsync() =>
        Interlocked.Exchange(ref _snapshotReleased, 1) == 0
            ? _releaseSnapshot()
            : ValueTask.CompletedTask;

    sealed class SyncAsyncEnumerator(IEnumerator<PantsEntry> entries) : IAsyncEnumerator<PantsEntry>
    {
        public PantsEntry Current => entries.Current;

        public ValueTask DisposeAsync()
        {
            entries.Dispose();
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(entries.MoveNext());
    }
}
