namespace Cntryl.Pants.Storage.Internal.Wal;

sealed class WalRecoveryStateMachine : IDisposable
{
    readonly Dictionary<(ulong WriterEpoch, ulong TransactionId), WalRecoverySpool>
        _openTransactions = [];

    bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var spool in _openTransactions.Values)
        {
            spool.Dispose();
        }

        _openTransactions.Clear();
    }

    public void Accept(
        WalRecord record,
        Action<WalMutation, ulong> apply)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(apply);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (record.Operation == WalOperation.TransactionBatch)
        {
            var mutations = WalCodec.DecodeTransactionBatch(
                record,
                out var commitSequence,
                out _);
            foreach (var mutation in mutations)
            {
                apply(mutation, commitSequence);
            }

            return;
        }

        if (record.Operation == WalOperation.TransactionBegin)
        {
            AcceptBegin(record);
            return;
        }

        if (record.Operation == WalOperation.TransactionCommit)
        {
            AcceptCommit(record, apply);
            return;
        }

        if (!WalCodec.IsMutation(record.Operation))
        {
            return;
        }

        if (record.TransactionId is { } transactionId &&
            _openTransactions.TryGetValue(
                (record.WriterEpoch, transactionId),
                out var spool))
        {
            spool.Append(record);
            return;
        }

        ApplyMutation(record, record.Sequence, apply);
    }

    void AcceptBegin(WalRecord record)
    {
        if (record.TransactionId is not { } transactionId)
        {
            return;
        }

        var key = (record.WriterEpoch, transactionId);
        if (_openTransactions.ContainsKey(key))
        {
            throw new StorageException("WAL contains a duplicate transaction begin record.");
        }

        _openTransactions.Add(key, new WalRecoverySpool());
    }

    void AcceptCommit(
        WalRecord record,
        Action<WalMutation, ulong> apply)
    {
        if (record.TransactionId is not { } transactionId ||
            !_openTransactions.Remove(
                (record.WriterEpoch, transactionId),
                out var spool))
        {
            return;
        }

        using (spool)
        {
            spool.Replay(spooled => ApplyMutation(spooled, record.Sequence, apply));
        }
    }

    static void ApplyMutation(
        WalRecord record,
        ulong commitSequence,
        Action<WalMutation, ulong> apply)
    {
        if (!WalCodec.TryDecodeMutation(record, out var mutation))
        {
            return;
        }

        apply(mutation, commitSequence);
    }
}
