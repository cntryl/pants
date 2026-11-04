namespace Cntryl.Pants.Storage.Internal.Wal;

sealed class WalRecoveryStateMachine : IDisposable
{
    readonly Dictionary<(ulong WriterEpoch, ulong TransactionId), WalRecoverySpool>
        _openTransactions = [];

    readonly StorageBudgetLedger? _ledger;
    readonly string _scratchDirectory;
    readonly long? _maximumReplayableTransactionBytes;
    readonly Dictionary<(ulong WriterEpoch, ulong TransactionId), long> _pendingTransactions = [];
    long _pendingBytes;
    ulong? _writerEpoch;

    bool _disposed;

    public bool HasOpenTransactions => _openTransactions.Count != 0;

    /// <summary>
    ///     <paramref name="scratchDirectory" /> holds the per-transaction spools. It belongs under
    ///     the database so the bytes land on the volume the operator sized for it, and so the
    ///     database's own cleanup owns them. <paramref name="ledger" />, when a local-storage budget
    ///     applies, is charged for every spooled byte until its transaction resolves.
    /// </summary>
    public WalRecoveryStateMachine(string scratchDirectory, StorageBudgetLedger? ledger = null,
        long? maximumReplayableTransactionBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scratchDirectory);
        _scratchDirectory = scratchDirectory;
        _ledger = ledger;
        _maximumReplayableTransactionBytes = maximumReplayableTransactionBytes;
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

    public void Accept(
        WalRecord record,
        Action<WalMutation, ulong> apply)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(apply);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_maximumReplayableTransactionBytes is not null && _writerEpoch != record.WriterEpoch)
        {
            foreach (var abandoned in _openTransactions.Values)
            {
                abandoned.Dispose();
            }
            _openTransactions.Clear();
            _pendingTransactions.Clear();
            _pendingBytes = 0;
            _writerEpoch = record.WriterEpoch;
        }

        if (record.Operation == WalOperation.TransactionBatch)
        {
            var mutations = WalCodec.DecodeTransactionBatch(
                record,
                _maximumReplayableTransactionBytes ?? WalCodec.DefaultTransactionBatchReplayBudget,
                out var commitSequence,
                out _, configuredReplayLimit: _maximumReplayableTransactionBytes is not null);
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
            if (record.ColumnFamilyId != spool.ColumnFamilyId)
            {
                throw new PantsCorruptionException("Split WAL transaction member belongs to another column family.");
            }

            ReservePending((record.WriterEpoch, transactionId),
                288L + record.Key.LongLength + (record.Value?.LongLength ?? 0) + (record.RangeEnd?.LongLength ?? 0));
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

        ReservePending(key, 128);
        _openTransactions.Add(key, new WalRecoverySpool(_scratchDirectory, _ledger, record.ColumnFamilyId));
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

        if (_pendingTransactions.Remove((record.WriterEpoch, transactionId), out var pendingBytes))
        {
            _pendingBytes -= pendingBytes;
        }
        using (spool)
        {
            if (record.ColumnFamilyId != spool.ColumnFamilyId)
            {
                throw new PantsCorruptionException("Split WAL transaction commits in another column family.");
            }

            spool.Replay(spooled => ApplyMutation(spooled, record.Sequence, apply));
        }
    }

    void ReservePending((ulong WriterEpoch, ulong TransactionId) key, long bytes)
    {
        if (_maximumReplayableTransactionBytes is not { } limit)
        {
            return;
        }
        if (bytes > limit - _pendingBytes)
        {
            throw new PantsResourceLimitException("Uncommitted WAL transactions exceed the configured replay buffer limit.");
        }
        _pendingBytes += bytes;
        _pendingTransactions[key] = _pendingTransactions.GetValueOrDefault(key) + bytes;
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
