namespace Pants;

internal sealed class PantsScanInstance : IPantsScan, IAsyncEnumerator<PantsEntry>
{
    private Func<IReadOnlyList<PantsEntry>>? _entriesFactory;
    private readonly Func<ValueTask> _releaseSnapshot;
    private readonly byte[]? _startInclusive;
    private readonly byte[]? _endExclusive;
    private readonly byte[]? _prefix;
    private readonly int _limit;
    private IReadOnlyList<PantsEntry>? _entries;
    private CancellationToken _cancellationToken;
    private Exception? _failure;
    private int _cursor;
    private int _emitted;
    private int _enumerationStarted;
    private int _disposed;
    private int _snapshotReleased;

    internal PantsScanInstance(
        Func<IReadOnlyList<PantsEntry>> entriesFactory,
        PantsScanQuery query,
        Func<ValueTask> releaseSnapshot)
    {
        _entriesFactory = entriesFactory ?? throw new ArgumentNullException(nameof(entriesFactory));
        _releaseSnapshot = releaseSnapshot ?? throw new ArgumentNullException(nameof(releaseSnapshot));
        Direction = query.Direction;
        _prefix = Copy(query.Prefix);
        _startInclusive = Maximum(Copy(query.StartInclusive), _prefix);
        _endExclusive = Minimum(Copy(query.EndExclusive), PrefixSuccessor(_prefix));
        _limit = query.Limit ?? int.MaxValue;
        if (!Enum.IsDefined(Direction))
        {
            throw PantsException.InvalidArgument("Scan direction is invalid.");
        }

        if (_limit < 0)
        {
            throw PantsException.InvalidArgument("Scan limit must not be negative.");
        }

        if (query.StartInclusive is { } start &&
            query.EndExclusive is { } end &&
            start.Span.SequenceCompareTo(end.Span) > 0)
        {
            throw PantsException.InvalidArgument(
                "Scan requires StartInclusive to be less than or equal to EndExclusive.");
        }
    }

    public PantsScanDirection Direction { get; }

    public PantsIteratorState State { get; private set; } = PantsIteratorState.Active;

    public bool IsActive => State == PantsIteratorState.Active;

    public bool IsExhausted => State == PantsIteratorState.Exhausted;

    public bool IsFailed => State == PantsIteratorState.Failed;

    public PantsEntry Current { get; private set; }

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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && State == PantsIteratorState.Active)
        {
            State = PantsIteratorState.Exhausted;
            Current = default;
        }

        await ReleaseSnapshotAsync().ConfigureAwait(false);
    }

    private async ValueTask<bool> MoveNextCoreAsync()
    {
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_emitted >= _limit)
            {
                await ExhaustAsync().ConfigureAwait(false);
                return false;
            }

            EnsureEntriesInitialized();
            while (IsCursorInRange())
            {
                PantsEntry candidate = _entries![_cursor];
                _cursor += Direction == PantsScanDirection.Forward ? 1 : -1;
                ReadOnlySpan<byte> key = candidate.Key.Span;
                if (!MatchesBounds(key) || !MatchesPrefix(key))
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

    private bool IsCursorInRange() => _entries is not null && _cursor >= 0 && _cursor < _entries.Count;

    private void EnsureEntriesInitialized()
    {
        if (_entries is not null)
        {
            return;
        }

        Func<IReadOnlyList<PantsEntry>> factory = _entriesFactory ??
            throw new PantsInternalException("The scan entry source is unavailable.");
        _entries = factory();
        _entriesFactory = null;
        _cursor = Direction == PantsScanDirection.Forward ? 0 : _entries.Count - 1;
    }

    private bool MatchesBounds(ReadOnlySpan<byte> key) =>
        (_startInclusive is null || key.SequenceCompareTo(_startInclusive) >= 0) &&
        (_endExclusive is null || key.SequenceCompareTo(_endExclusive) < 0);

    private bool MatchesPrefix(ReadOnlySpan<byte> key) =>
        _prefix is null || key.StartsWith(_prefix);

    private async ValueTask ExhaustAsync()
    {
        State = PantsIteratorState.Exhausted;
        Current = default;
        await ReleaseSnapshotAsync().ConfigureAwait(false);
    }

    private void Fail(Exception exception)
    {
        _failure ??= exception;
        State = PantsIteratorState.Failed;
        Current = default;
    }

    private ValueTask ReleaseSnapshotAsync() =>
        Interlocked.Exchange(ref _snapshotReleased, 1) == 0
            ? _releaseSnapshot()
            : ValueTask.CompletedTask;

    private static byte[]? Maximum(byte[]? left, byte[]? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left.AsSpan().SequenceCompareTo(right) >= 0 ? left : right;
    }

    private static byte[]? Minimum(byte[]? left, byte[]? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left.AsSpan().SequenceCompareTo(right) <= 0 ? left : right;
    }

    private static byte[]? PrefixSuccessor(byte[]? prefix)
    {
        if (prefix is null || prefix.Length == 0)
        {
            return null;
        }

        byte[] successor = prefix.ToArray();
        for (int index = successor.Length - 1; index >= 0; index--)
        {
            if (successor[index] == byte.MaxValue)
            {
                continue;
            }

            successor[index]++;
            return successor[..(index + 1)];
        }

        return null;
    }

    private static byte[]? Copy(ReadOnlyMemory<byte>? value) =>
        value.HasValue ? value.Value.ToArray() : null;
}
