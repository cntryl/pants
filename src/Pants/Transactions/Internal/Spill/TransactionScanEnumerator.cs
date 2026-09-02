using System.Collections;
using System.Collections.Immutable;

namespace Cntryl.Pants.Transactions.Internal.Spill;

sealed class TransactionScanEnumerator : IEnumerator<PantsEntry>
{
    readonly PantsScanDirection _direction;
    readonly IEnumerator<byte[]> _intentKeys;
    readonly TransactionIntentReadView _intents;
    readonly ImmutableSortedDictionary<byte[], CellState> _snapshot;
    readonly IEnumerator<byte[]> _snapshotKeys;
    readonly DateTimeOffset _snapshotTime;
    readonly IReadOnlyList<SstScanSource> _sstSources;
    readonly IReadOnlyList<RangeTombstone> _sstRangeTombstones;
    readonly SstEntry?[] _sstHeads;
    bool _advanceIntent;
    bool _advanceSnapshot;
    int _disposed;
    bool _initialized;
    byte[]? _intentHead;
    byte[]? _snapshotHead;

    public TransactionScanEnumerator(
        DatabaseVersion snapshot,
        ColumnFamilyIdentity family,
        DateTimeOffset snapshotTime,
        TransactionIntentReadView intents,
        ScanBounds bounds,
        IReadOnlyList<SstScanSource>? sstSources = null)
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
        _sstSources = sstSources ?? [];
        _sstRangeTombstones = _sstSources.SelectMany(static source => source.RangeTombstones).ToArray();
        _sstHeads = new SstEntry?[_sstSources.Count];
    }

    public PantsEntry Current { get; private set; }

    object IEnumerator.Current => Current;

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

            // Always advance every SST source whose head is at this key, even when the
            // in-memory tier also covers it below and wins — otherwise a stale SST entry for an
            // already-in-memory key would never advance past and the scan would stall.
            var sstValue = ResolveFromSstSources(key);

            byte[]? baseValue;
            if (_snapshotHead is not null && ByteArrayComparer.Instance.Equals(_snapshotHead, key))
            {
                // A key resident in the in-memory tier is always at least as new as any SST
                // copy of it (see RuntimeState.ReleaseFlushedGeneration) — no need to compare
                // against SST candidates at the same key when this source has it.
                baseValue = null;
                if (_snapshot.TryGetValue(key, out var cell) &&
                    cell.Value is not null &&
                    !cell.IsExpired(_snapshotTime))
                {
                    baseValue = cell.Value;
                }

                _advanceSnapshot = true;
            }
            else
            {
                baseValue = sstValue;
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
            foreach (var source in _sstSources)
            {
                source.Dispose();
            }
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
        for (var index = 0; index < _sstSources.Count; index++)
        {
            _sstHeads[index] = _sstSources[index].Iterator.MoveNext()
                ? _sstSources[index].Iterator.Current
                : null;
        }

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

    /// <summary>
    /// Picks the newest (highest-sequence) non-tombstone, non-expired entry among every SST
    /// source whose head is at <paramref name="key"/>, then advances exactly those sources —
    /// a key can legitimately appear at the head of more than one SST at once.
    /// </summary>
    byte[]? ResolveFromSstSources(byte[] key)
    {
        SstEntry? best = null;
        for (var index = 0; index < _sstHeads.Length; index++)
        {
            var head = _sstHeads[index];
            if (head is null || !head.Key.AsSpan().SequenceEqual(key))
            {
                continue;
            }

            if (best is null || head.Sequence > best.Sequence)
            {
                best = head;
            }

            _sstHeads[index] = _sstSources[index].Iterator.MoveNext()
                ? _sstSources[index].Iterator.Current
                : null;
        }

        if (best is null ||
            best.IsDelete ||
            UnixTimestamp.IsExpired(best.Expiration, _snapshotTime) ||
            SstRangeTombstoneMask.Covers(_sstRangeTombstones, key, best.Sequence))
        {
            return null;
        }

        return best.Value;
    }

    byte[]? SelectKey()
    {
        byte[]? candidate = _snapshotHead;
        candidate = Combine(candidate, _intentHead);
        foreach (var head in _sstHeads)
        {
            candidate = Combine(candidate, head?.Key);
        }

        return candidate;
    }

    byte[]? Combine(byte[]? left, byte[]? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        var comparison = ByteArrayComparer.Instance.Compare(right, left);
        var rightWins = _direction == PantsScanDirection.Reverse ? comparison > 0 : comparison < 0;
        return rightWins ? right : left;
    }
}
