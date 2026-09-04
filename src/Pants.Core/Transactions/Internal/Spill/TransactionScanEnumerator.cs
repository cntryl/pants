using System.Collections.Immutable;

namespace Cntryl.Pants.Transactions.Internal.Spill;

sealed class TransactionScanEnumerator : IAsyncEnumerator<PantsEntry>
{
    readonly CancellationToken _cancellationToken;
    readonly PantsScanDirection _direction;
    readonly IEnumerator<byte[]> _intentKeys;
    readonly TransactionIntentReadView _intents;
    readonly ImmutableSortedDictionary<byte[], CellState> _snapshot;
    readonly IEnumerator<byte[]> _snapshotKeys;
    readonly IReadOnlyList<CommittedRangeTombstone> _snapshotRangeTombstones;
    readonly DateTimeOffset _snapshotTime;
    readonly bool[] _sstActivated;
    readonly SstEntry?[] _sstHeads;
    readonly IReadOnlyList<RangeTombstone> _sstRangeTombstones;
    readonly AsyncSstScanSource?[] _sstSources;
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
        IReadOnlyList<AsyncSstScanSource>? sstSources = null,
        CancellationToken cancellationToken = default)
    {
        if (!snapshot.Families.TryGetValue(family, out _snapshot!))
        {
            throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Column family '{family.Name}' is not in the transaction snapshot.");
        }

        _snapshotTime = snapshotTime;
        _cancellationToken = cancellationToken;
        _intents = intents;
        _direction = bounds.Direction;
        var snapshotKeys = _snapshot.Keys.Where(key => bounds.Matches(key));
        if (bounds.Direction == PantsScanDirection.Reverse)
        {
            snapshotKeys = snapshotKeys.Reverse();
        }

        _snapshotKeys = snapshotKeys.GetEnumerator();
        _snapshotRangeTombstones = snapshot.RangeTombstones[family];
        _intentKeys = intents.CreateKeyScan(
            bounds.StartInclusive,
            bounds.EndExclusive,
            bounds.Direction);
        _sstSources = (sstSources ?? []).Cast<AsyncSstScanSource?>().ToArray();
        _sstActivated = new bool[_sstSources.Length];
        _sstRangeTombstones = _sstSources
            .SelectMany(static source => source!.RangeTombstones)
            .ToArray();
        _sstHeads = new SstEntry?[_sstSources.Length];
    }

    public PantsEntry Current { get; private set; }

    public async ValueTask<bool> MoveNextAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await InitializeAsync().ConfigureAwait(false);
        while (true)
        {
            AdvanceConsumedSources();
            await ActivateSourcesThroughFrontierAsync().ConfigureAwait(false);
            var key = SelectKey();
            if (key is null)
            {
                Current = default;
                return false;
            }

            var sstValue = await ResolveFromSstSourcesAsync(key).ConfigureAwait(false);

            byte[]? baseValue;
            if (_snapshotHead is not null && ByteArrayComparer.Instance.Equals(_snapshotHead, key))
            {
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _snapshotKeys.Dispose();
        _intentKeys.Dispose();
        for (var index = 0; index < _sstSources.Length; index++)
        {
            await DisposeSourceAsync(index).ConfigureAwait(false);
        }
    }

    async ValueTask InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _snapshotHead = _snapshotKeys.MoveNext() ? _snapshotKeys.Current : null;
        _intentHead = _intentKeys.MoveNext() ? _intentKeys.Current : null;
        _initialized = true;
    }

    async ValueTask ActivateSourcesThroughFrontierAsync()
    {
        while (TryGetNextInactiveSource(out var index, out var activationKey))
        {
            var frontier = SelectKey();
            if (frontier is not null)
            {
                var comparison = ByteArrayComparer.Instance.Compare(activationKey, frontier);
                var canAffectFrontier = _direction == PantsScanDirection.Forward
                    ? comparison <= 0
                    : comparison >= 0;
                if (!canAffectFrontier)
                {
                    return;
                }
            }

            _sstActivated[index] = true;
            var source = _sstSources[index]!;
            if (await source.MoveNextAsync(_cancellationToken).ConfigureAwait(false))
            {
                _sstHeads[index] = source.Current;
            }
            else
            {
                await DisposeSourceAsync(index).ConfigureAwait(false);
            }
        }
    }

    bool TryGetNextInactiveSource(out int sourceIndex, out byte[] activationKey)
    {
        sourceIndex = -1;
        activationKey = null!;
        for (var index = 0; index < _sstSources.Length; index++)
        {
            if (_sstActivated[index] || _sstSources[index] is not { } source)
            {
                continue;
            }

            var candidate = _direction == PantsScanDirection.Forward
                ? source.SmallestKey
                : source.LargestKey;
            if (sourceIndex < 0)
            {
                sourceIndex = index;
                activationKey = candidate;
                continue;
            }

            var comparison = ByteArrayComparer.Instance.Compare(candidate, activationKey);
            if ((_direction == PantsScanDirection.Forward && comparison < 0) ||
                (_direction == PantsScanDirection.Reverse && comparison > 0))
            {
                sourceIndex = index;
                activationKey = candidate;
            }
        }

        return sourceIndex >= 0;
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

    async ValueTask<byte[]?> ResolveFromSstSourcesAsync(byte[] key)
    {
        SstEntry? best = null;
        for (var index = 0; index < _sstHeads.Length; index++)
        {
            var head = _sstHeads[index];
            if (head is null || !head.Key.AsSpan().SequenceEqual(key))
            {
                continue;
            }

            var source = _sstSources[index]!;
            do
            {
                if (best is null || head.Sequence > best.Sequence)
                {
                    best = head;
                }

                if (await source.MoveNextAsync(_cancellationToken).ConfigureAwait(false))
                {
                    _sstHeads[index] = source.Current;
                }
                else
                {
                    _sstHeads[index] = null;
                    await DisposeSourceAsync(index).ConfigureAwait(false);
                }

                head = _sstHeads[index];
            } while (head is not null && head.Key.AsSpan().SequenceEqual(key));
        }

        if (best is null ||
            best.IsDelete ||
            UnixTimestamp.IsExpired(best.Expiration, _snapshotTime) ||
            SstRangeTombstoneMask.Covers(_sstRangeTombstones, key, best.Sequence) ||
            _snapshotRangeTombstones.Any(tombstone =>
                tombstone.WriteSequence > checked((long)best.Sequence) &&
                ByteArrayComparer.Instance.Compare(key, tombstone.Start) >= 0 &&
                ByteArrayComparer.Instance.Compare(key, tombstone.EndExclusive) < 0))
        {
            return null;
        }

        return best.Value;
    }

    async ValueTask DisposeSourceAsync(int index)
    {
        if (_sstSources[index] is not { } source)
        {
            return;
        }

        _sstSources[index] = null;
        await source.DisposeAsync().ConfigureAwait(false);
    }

    byte[]? SelectKey()
    {
        var candidate = _snapshotHead;
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
