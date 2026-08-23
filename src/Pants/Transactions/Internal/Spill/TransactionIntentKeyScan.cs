using System.Collections;

namespace Cntryl.Pants.Transactions.Internal.Spill;

sealed class TransactionIntentKeyScan : IEnumerator<byte[]>
{
    readonly PantsScanDirection _direction;
    readonly byte[]?[] _heads;
    readonly bool[] _needsAdvance;
    readonly IReadOnlyList<IEnumerator<byte[]>> _sources;
    int _disposed;
    bool _exhausted;
    bool _primed;

    public TransactionIntentKeyScan(
        IReadOnlyList<IEnumerator<byte[]>> sources,
        PantsScanDirection direction)
    {
        _sources = sources;
        _heads = new byte[sources.Count][];
        _needsAdvance = new bool[sources.Count];
        _direction = direction;
    }

    public byte[] Current { get; private set; } = [];

    object IEnumerator.Current => Current;

    public bool MoveNext()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_exhausted)
        {
            return false;
        }

        for (var index = 0; index < _sources.Count; index++)
        {
            if (!_primed || _needsAdvance[index])
            {
                _heads[index] = _sources[index].MoveNext()
                    ? _sources[index].Current
                    : null;
                _needsAdvance[index] = false;
            }
        }

        _primed = true;
        byte[]? selected = null;
        foreach (var head in _heads)
        {
            if (head is null)
            {
                continue;
            }

            if (selected is null)
            {
                selected = head;
                continue;
            }

            var comparison = ByteArrayComparer.Instance.Compare(head, selected);
            if ((_direction == PantsScanDirection.Forward && comparison < 0) ||
                (_direction == PantsScanDirection.Reverse && comparison > 0))
            {
                selected = head;
            }
        }

        if (selected is null)
        {
            _exhausted = true;
            Current = [];
            return false;
        }

        for (var index = 0; index < _heads.Length; index++)
        {
            if (_heads[index] is { } head &&
                ByteArrayComparer.Instance.Equals(head, selected))
            {
                _needsAdvance[index] = true;
            }
        }

        Current = selected;
        return true;
    }

    public void Reset() => throw new NotSupportedException();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var source in _sources)
        {
            source.Dispose();
        }
    }
}
