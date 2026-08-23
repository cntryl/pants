namespace Cntryl.Pants;

internal sealed class TransactionScanEnumerator : IEnumerator<PantsEntry>
{
    readonly SortedDictionary<byte[], CellState> _snapshot;
    readonly DateTimeOffset _snapshotTime;
    readonly TransactionIntentReadView _intents;
    readonly IEnumerator<byte[]> _snapshotKeys;
    readonly IEnumerator<byte[]> _intentKeys;
    readonly PantsScanDirection _direction;
    byte[]? _snapshotHead;
    byte[]? _intentHead;
    bool _initialized;
    bool _advanceSnapshot;
    bool _advanceIntent;
    int _disposed;

    public TransactionScanEnumerator(
        DatabaseSnapshot snapshot,
        ColumnFamilyIdentity family,
        DateTimeOffset snapshotTime,
        TransactionIntentReadView intents,
        PantsScanBounds bounds)
    {
        if (!snapshot.Families.TryGetValue(family, out _snapshot!))
        {
            throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Column family '{family.Name}' is not in the transaction snapshot.");
        }

        _snapshotTime = snapshotTime;
        _intents = intents;
        _direction = bounds.Direction;
        var snapshotKeys = _snapshot.Keys.Where(key => bounds.Matches(key));
        if (bounds.Direction == PantsScanDirection.Reverse)
        {
            snapshotKeys = snapshotKeys.Reverse();
        }

        _snapshotKeys = snapshotKeys.GetEnumerator();
        _intentKeys = intents.CreateKeyScan(
            bounds.StartInclusive,
            bounds.EndExclusive,
            bounds.Direction);
    }

    public PantsEntry Current { get; private set; }

    object System.Collections.IEnumerator.Current => Current;

    public bool MoveNext()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Initialize();
        while (true)
        {
            AdvanceConsumedSources();
            var key = SelectKey();
            if (key is null)
            {
                Current = default;
                return false;
            }

            byte[]? baseValue = null;
            if (_snapshotHead is not null && ByteArrayComparer.Instance.Equals(_snapshotHead, key))
            {
                if (_snapshot.TryGetValue(key, out var cell) &&
                    cell.Value is not null &&
                    !cell.IsExpired(_snapshotTime))
                {
                    baseValue = cell.Value;
                }

                _advanceSnapshot = true;
            }

            if (_intentHead is not null && ByteArrayComparer.Instance.Equals(_intentHead, key))
            {
                _advanceIntent = true;
            }

            var lookup = _intents.LookupLatest(key);
            var value = lookup switch
            {
                { IsDeleted: true } => null,
                { Value: { } intentValue } => intentValue,
                _ => baseValue
            };
            if (value is null)
            {
                continue;
            }

            Current = new PantsEntry(key.ToArray(), value.ToArray());
            return true;
        }
    }

    public void Reset() => throw new NotSupportedException();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _snapshotKeys.Dispose();
            _intentKeys.Dispose();
        }
    }

    void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _snapshotHead = _snapshotKeys.MoveNext() ? _snapshotKeys.Current : null;
        _intentHead = _intentKeys.MoveNext() ? _intentKeys.Current : null;
        _initialized = true;
    }

    void AdvanceConsumedSources()
    {
        if (_advanceSnapshot)
        {
            _snapshotHead = _snapshotKeys.MoveNext() ? _snapshotKeys.Current : null;
            _advanceSnapshot = false;
        }

        if (_advanceIntent)
        {
            _intentHead = _intentKeys.MoveNext() ? _intentKeys.Current : null;
            _advanceIntent = false;
        }
    }

    byte[]? SelectKey()
    {
        if (_snapshotHead is null)
        {
            return _intentHead;
        }

        if (_intentHead is null)
        {
            return _snapshotHead;
        }

        var comparison = ByteArrayComparer.Instance.Compare(_intentHead, _snapshotHead);
        var intentWins = _direction == PantsScanDirection.Reverse
            ? comparison > 0
            : comparison < 0;
        return intentWins ? _intentHead : _snapshotHead;
    }
}
