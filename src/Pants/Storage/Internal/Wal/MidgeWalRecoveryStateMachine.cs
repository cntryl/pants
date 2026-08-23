namespace Cntryl.Pants;

internal sealed class MidgeWalRecoveryStateMachine : IDisposable
{
    readonly Dictionary<(ulong WriterEpoch, ulong TransactionId), MidgeWalRecoverySpool>
        _openTransactions = [];
    bool _disposed;

    public void Accept(
        MidgeWalRecord record,
        Action<MidgeWalMutation, ulong> apply)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(apply);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (record.Operation == MidgeWalOperation.TransactionBatch)
        {
            var mutations = MidgeWalCodec.DecodeTransactionBatch(
                record,
                out var commitSequence,
                out _);
            foreach (var mutation in mutations)
            {
                apply(mutation, commitSequence);
            }

            return;
        }

        if (record.Operation == MidgeWalOperation.TransactionBegin)
        {
            AcceptBegin(record);
            return;
        }

        if (record.Operation == MidgeWalOperation.TransactionCommit)
        {
            AcceptCommit(record, apply);
            return;
        }

        if (!MidgeWalCodec.IsMutation(record.Operation))
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

    void AcceptBegin(MidgeWalRecord record)
    {
        if (record.TransactionId is not { } transactionId)
        {
            return;
        }

        var key = (record.WriterEpoch, transactionId);
        if (_openTransactions.ContainsKey(key))
        {
            throw new PantsStorageException("WAL contains a duplicate transaction begin record.");
        }

        _openTransactions.Add(key, new MidgeWalRecoverySpool());
    }

    void AcceptCommit(
        MidgeWalRecord record,
        Action<MidgeWalMutation, ulong> apply)
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
        MidgeWalRecord record,
        ulong commitSequence,
        Action<MidgeWalMutation, ulong> apply)
    {
        if (!MidgeWalCodec.TryDecodeMutation(record, out var mutation))
        {
            return;
        }

        apply(mutation, commitSequence);
    }

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
}
