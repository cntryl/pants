using System.Collections;

namespace Cntryl.Pants.Transactions.Internal.Spill;

sealed class TransactionIntentResidentKeyCursor : IEnumerator<byte[]>
{
    readonly PantsScanDirection _direction;
    readonly byte[]? _endExclusive;
    readonly byte[]? _startInclusive;
    readonly TransactionIntentOperation[] _operations;
    int _index;
    byte[]? _lastKey;
    int _disposed;

    public TransactionIntentResidentKeyCursor(
        TransactionIntentOperation[] operations,
        byte[]? startInclusive,
        byte[]? endExclusive,
        PantsScanDirection direction)
    {
        _operations = operations;
        _startInclusive = startInclusive;
        _endExclusive = endExclusive;
        _direction = direction;
        _index = direction == PantsScanDirection.Forward ? 0 : operations.Length - 1;
    }

    public byte[] Current { get; private set; } = [];

    object IEnumerator.Current => Current;

    public bool MoveNext()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        while ((uint)_index < (uint)_operations.Length)
        {
            var operation = _operations[_index];
            _index += _direction == PantsScanDirection.Forward ? 1 : -1;
            var key = operation.Key;
            var startOrder = _startInclusive is null
                ? 0
                : ByteArrayComparer.Instance.Compare(key, _startInclusive);
            var endOrder = _endExclusive is null
                ? -1
                : ByteArrayComparer.Instance.Compare(key, _endExclusive);

            if (_direction == PantsScanDirection.Forward)
            {
                if (startOrder < 0)
                {
                    continue;
                }

                if (endOrder >= 0)
                {
                    break;
                }
            }
            else
            {
                if (endOrder >= 0)
                {
                    continue;
                }

                if (startOrder < 0)
                {
                    break;
                }
            }

            if (_lastKey is not null && ByteArrayComparer.Instance.Equals(_lastKey, key))
            {
                continue;
            }

            _lastKey = key;
            Current = key;
            return true;
        }

        Current = [];
        return false;
    }

    public void Reset() => throw new NotSupportedException();

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }
}
