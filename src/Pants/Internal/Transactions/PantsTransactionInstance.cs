using System.Diagnostics;

namespace Pants;

internal sealed class PantsTransactionInstance : IPantsTransaction
{
    private const int OperationAccountingOverhead = 64;
    private const int AssertionAccountingOverhead = 48;

    private readonly PantsDatabaseInstance _database;
    private readonly long _transactionId;
    private readonly PantsColumnFamilyHandle _columnFamily;
    private readonly PantsTransactionMode _mode;
    private readonly DatabaseSnapshot _startSnapshot;
    private readonly DateTimeOffset _snapshotTime;
    private readonly TransactionSpillStore? _spillStore;
    private readonly List<TransactionIntentOperation> _intentLog = [];
    private readonly List<TransactionAssertion> _assertions = [];
    private readonly object _gate = new();
    private PantsConflictPolicy _conflictPolicy = PantsConflictPolicy.LastWriteWins;
    private long _residentIntentBytes;
    private long _assertionBytes;
    private ulong _nextOrdinal;
    private int _state;

    internal PantsTransactionInstance(
        PantsDatabaseInstance database,
        long transactionId,
        PantsColumnFamilyHandle columnFamily,
        PantsTransactionMode mode,
        DatabaseSnapshot startSnapshot,
        DateTimeOffset snapshotTime,
        string? persistentDatabasePath)
    {
        _database = database;
        _transactionId = transactionId;
        _columnFamily = columnFamily;
        _mode = mode;
        _startSnapshot = startSnapshot;
        _snapshotTime = snapshotTime;
        _spillStore = persistentDatabasePath is null
            ? null
            : new TransactionSpillStore(persistentDatabasePath, transactionId, columnFamily.Identity);
    }

    public IPantsColumnFamily ColumnFamily => _columnFamily;

    public PantsTransactionMode Mode => _mode;

    public PantsConflictPolicy ConflictPolicy
    {
        get
        {
            lock (_gate)
            {
                return _conflictPolicy;
            }
        }
    }

    public void SetConflictPolicy(PantsConflictPolicy conflictPolicy)
    {
        lock (_gate)
        {
            EnsureActive();
            if (!Enum.IsDefined(conflictPolicy))
            {
                throw PantsException.InvalidArgument("Conflict policy is invalid.");
            }

            _conflictPolicy = conflictPolicy;
        }
    }

    public void Put(
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value,
        TimeSpan? timeToLive = null) =>
        StagePointWrite(key, value, timeToLive, insertOnly: false);

    public void Insert(
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value,
        TimeSpan? timeToLive = null) =>
        StagePointWrite(key, value, timeToLive, insertOnly: true);

    public void Delete(ReadOnlyMemory<byte> key)
    {
        lock (_gate)
        {
            EnsureWritable();
            byte[] keyCopy = key.ToArray();
            StageIntent(new TransactionIntentOperation(
                _nextOrdinal,
                CommitOperationKind.Delete,
                _columnFamily.Identity,
                keyCopy,
                null,
                null,
                null,
                null,
                false),
                checked(keyCopy.Length + OperationAccountingOverhead));
        }
    }

    public void DeleteRange(
        ReadOnlyMemory<byte> startInclusive,
        ReadOnlyMemory<byte> endExclusive)
    {
        lock (_gate)
        {
            EnsureWritable();
            byte[] startCopy = startInclusive.ToArray();
            byte[] endCopy = endExclusive.ToArray();
            if (ByteArrayComparer.Instance.Compare(startCopy, endCopy) > 0)
            {
                throw PantsException.InvalidArgument(
                    "DeleteRange requires startInclusive <= endExclusive.");
            }

            StageIntent(new TransactionIntentOperation(
                _nextOrdinal,
                CommitOperationKind.DeleteRange,
                _columnFamily.Identity,
                startCopy,
                endCopy,
                null,
                null,
                null,
                false),
                checked(startCopy.Length + endCopy.Length + OperationAccountingOverhead));
        }
    }

    public void AssertValue(
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte>? expectedValue)
    {
        lock (_gate)
        {
            EnsureWritable();
            byte[] keyCopy = key.ToArray();
            byte[]? valueCopy = expectedValue?.ToArray();
            ReserveAssertion(checked(
                keyCopy.Length +
                (valueCopy?.Length ?? 0) +
                AssertionAccountingOverhead));
            _assertions.Add(new TransactionAssertion(
                keyCopy,
                valueCopy is null
                    ? new TransactionReadValue(null, true)
                    : new TransactionReadValue(valueCopy, false)));
        }
    }

    public ValueTask<ReadOnlyMemory<byte>?> GetAsync(
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureActive();
            _database.EnsureOpen();
            byte[] keyCopy = key.ToArray();
            byte[]? value = ReadVisibleValue(keyCopy);
            if (value is null)
            {
                return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
            }

            return ValueTask.FromResult<ReadOnlyMemory<byte>?>(value.ToArray());
        }
    }

    public async ValueTask<IPantsScan> ScanAsync(
        PantsScanQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        TransactionIntentOperation[] operations;
        lock (_gate)
        {
            EnsureActive();
            _database.EnsureOpen();
            operations = [.. LoadOrderedIntents()];
        }

        long snapshotId = await _database
            .RegisterScanSnapshotAsync(_startSnapshot, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return new PantsScanInstance(
                () => ComposeVisibleSnapshot(operations),
                query,
                () => _database.ReleaseScanSnapshotAsync(snapshotId));
        }
        catch
        {
            await _database.ReleaseScanSnapshotAsync(snapshotId).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask CommitAsync(
        PantsWriteOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (_mode != PantsTransactionMode.ReadOnly &&
            !_database.IsSupported(options.Durability))
        {
            throw PantsException.Create(
                PantsErrorCode.NotSupported,
                $"Durability '{options.Durability}' is not supported by this storage backend.");
        }

        lock (_gate)
        {
            EnsureActive();
            _database.EnsureOpen();
            _state = 1;
        }

        using Activity? activity = PantsDiagnostics.ActivitySource.StartActivity("PantsTransaction.Commit");
        try
        {
            await _database.CommitTransactionAsync(this, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await _database.RollbackTransactionAsync(_transactionId, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            Finish();
        }
    }

    public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_state == 2)
            {
                return;
            }

            if (_state == 1)
            {
                throw PantsException.Create(
                    PantsErrorCode.Busy,
                    "The transaction is currently committing.");
            }

            _state = 1;
        }

        using Activity? activity = PantsDiagnostics.ActivitySource.StartActivity("PantsTransaction.Rollback");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _database.RollbackTransactionAsync(_transactionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await _database.RollbackTransactionAsync(_transactionId, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            Finish();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _state) == 0)
        {
            await RollbackAsync().ConfigureAwait(false);
        }
    }

    internal CommitPayload BuildCommitPayload()
    {
        lock (_gate)
        {
            if (_state != 1)
            {
                throw PantsException.Create(
                    PantsErrorCode.Internal,
                    "A commit payload was requested outside the commit phase.");
            }

            DateTimeOffset commitTime = _database.Clock.UtcNow;
            TransactionIntentOperation[] operations = LoadOrderedIntents()
                .Select(operation => new TransactionIntentOperation(
                    operation.Ordinal,
                    operation.Kind,
                    operation.Family,
                    operation.Key.ToArray(),
                    operation.EndExclusive?.ToArray(),
                    operation.Value?.ToArray(),
                    null,
                    CalculateExpiration(commitTime, operation.TimeToLive),
                    operation.InsertOnly))
                .ToArray();
            var familyWrites = new Dictionary<byte[], TransactionPendingWrite>(ByteArrayComparer.Instance);
            var familyDeleteRanges = new List<DeleteRange>();
            foreach (TransactionIntentOperation operation in operations)
            {
                switch (operation.Kind)
                {
                    case CommitOperationKind.Put:
                        familyWrites[operation.Key.ToArray()] = new TransactionPendingWrite(
                            operation.Value?.ToArray(),
                            operation.ExpiryUtc,
                            false,
                            operation.InsertOnly,
                            false);
                        break;
                    case CommitOperationKind.Delete:
                        familyWrites[operation.Key.ToArray()] = new TransactionPendingWrite(
                            null,
                            null,
                            true,
                            false,
                            false);
                        break;
                    case CommitOperationKind.DeleteRange when operation.EndExclusive is not null:
                        familyDeleteRanges.Add(new DeleteRange(
                            operation.Key.ToArray(),
                            operation.EndExclusive.ToArray()));
                        break;
                    default:
                        throw PantsException.Create(
                            PantsErrorCode.Internal,
                            "The transaction contains an invalid operation.");
                }
            }

            var writes = new Dictionary<ColumnFamilyIdentity, Dictionary<byte[], TransactionPendingWrite>>(
                ColumnFamilyIdentityComparer.Instance)
            {
                [_columnFamily.Identity] = familyWrites
            };
            var deleteRanges = new Dictionary<ColumnFamilyIdentity, List<DeleteRange>>(
                ColumnFamilyIdentityComparer.Instance)
            {
                [_columnFamily.Identity] = familyDeleteRanges
            };
            var assertions = new Dictionary<ColumnFamilyIdentity, IReadOnlyList<TransactionAssertion>>(
                ColumnFamilyIdentityComparer.Instance)
            {
                [_columnFamily.Identity] = _assertions
                    .Select(static assertion => new TransactionAssertion(
                        assertion.Key.ToArray(),
                        assertion.Expected.Missing
                            ? new TransactionReadValue(null, true)
                            : new TransactionReadValue(
                                assertion.Expected.Value!.ToArray(),
                                false)))
                    .ToArray()
            };
            return new CommitPayload(
                _transactionId,
                _mode,
                _conflictPolicy,
                _snapshotTime,
                _startSnapshot,
                operations,
                writes,
                deleteRanges,
                new Dictionary<ColumnFamilyIdentity, Dictionary<byte[], TransactionReadValue>>(
                    ColumnFamilyIdentityComparer.Instance),
                assertions);
        }
    }

    private void StagePointWrite(
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value,
        TimeSpan? timeToLive,
        bool insertOnly)
    {
        lock (_gate)
        {
            EnsureWritable();
            ValidateTimeToLive(timeToLive);
            byte[] keyCopy = key.ToArray();
            byte[] valueCopy = value.ToArray();
            StageIntent(new TransactionIntentOperation(
                _nextOrdinal,
                CommitOperationKind.Put,
                _columnFamily.Identity,
                keyCopy,
                null,
                valueCopy,
                timeToLive is null or { Ticks: 0 } ? null : timeToLive,
                null,
                insertOnly),
                checked(keyCopy.Length + valueCopy.Length + OperationAccountingOverhead));
        }
    }

    private byte[]? ReadVisibleValue(byte[] key)
    {
        List<TransactionIntentOperation> operations = LoadOrderedIntents();
        for (int index = operations.Count - 1; index >= 0; index--)
        {
            TransactionIntentOperation operation = operations[index];
            switch (operation.Kind)
            {
                case CommitOperationKind.Put when ByteArrayComparer.Instance.Equals(operation.Key, key):
                    return operation.Value?.ToArray();
                case CommitOperationKind.Delete when ByteArrayComparer.Instance.Equals(operation.Key, key):
                    return null;
                case CommitOperationKind.DeleteRange when
                    operation.EndExclusive is not null &&
                    IsInRange(key, operation.Key, operation.EndExclusive):
                    return null;
            }
        }

        if (!_startSnapshot.Families.TryGetValue(_columnFamily.Identity, out var family) ||
            !family.TryGetValue(key, out CellState? cell) ||
            cell.Value is null ||
            cell.IsExpired(_snapshotTime))
        {
            return null;
        }

        return cell.Value.ToArray();
    }

    private PantsEntry[] ComposeVisibleSnapshot(
        IReadOnlyList<TransactionIntentOperation> operations)
    {
        if (!_startSnapshot.Families.TryGetValue(_columnFamily.Identity, out var family))
        {
            throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Column family '{_columnFamily.Name}' is not in the transaction snapshot.");
        }

        var visible = new SortedDictionary<byte[], byte[]>(ByteArrayComparer.Instance);
        foreach ((byte[] key, CellState cell) in family)
        {
            if (cell.Value is not null && !cell.IsExpired(_snapshotTime))
            {
                visible[key.ToArray()] = cell.Value.ToArray();
            }
        }

        foreach (TransactionIntentOperation operation in operations)
        {
            switch (operation.Kind)
            {
                case CommitOperationKind.Put:
                    if (operation.Value is not null)
                    {
                        visible[operation.Key.ToArray()] = operation.Value.ToArray();
                    }

                    break;
                case CommitOperationKind.Delete:
                    visible.Remove(operation.Key);
                    break;
                case CommitOperationKind.DeleteRange when operation.EndExclusive is not null:
                    foreach (byte[] key in visible.Keys
                                 .Where(key => IsInRange(key, operation.Key, operation.EndExclusive))
                                 .ToArray())
                    {
                        visible.Remove(key);
                    }

                    break;
            }
        }

        return visible
            .Select(static pair => new PantsEntry(pair.Key.ToArray(), pair.Value.ToArray()))
            .ToArray();
    }

    private void StageIntent(TransactionIntentOperation operation, long bytes)
    {
        if (_database.TransactionMemoryPool.TryReserve(bytes))
        {
            _intentLog.Add(operation);
            _residentIntentBytes = checked(_residentIntentBytes + bytes);
            _nextOrdinal++;
            return;
        }

        if (_spillStore is null)
        {
            throw PantsException.Create(
                PantsErrorCode.ResourceLimit,
                $"The transaction memory pool cannot admit {bytes} bytes.");
        }

        if (_intentLog.Count != 0)
        {
            _spillStore.WriteRun(_intentLog);
            _intentLog.Clear();
            _database.TransactionMemoryPool.Release(_residentIntentBytes);
            _residentIntentBytes = 0;
        }

        if (_database.TransactionMemoryPool.TryReserve(bytes))
        {
            _intentLog.Add(operation);
            _residentIntentBytes = checked(_residentIntentBytes + bytes);
        }
        else
        {
            _spillStore.WriteRun([operation]);
        }

        _nextOrdinal++;
    }

    private void ReserveAssertion(long bytes)
    {
        if (!_database.TransactionMemoryPool.TryReserve(bytes))
        {
            throw PantsException.Create(
                PantsErrorCode.ResourceLimit,
                $"The transaction memory pool cannot admit {bytes} assertion bytes.");
        }

        _assertionBytes = checked(_assertionBytes + bytes);
    }

    private List<TransactionIntentOperation> LoadOrderedIntents()
    {
        if (_spillStore is null || !_spillStore.HasRuns)
        {
            return _intentLog;
        }

        var operations = new List<TransactionIntentOperation>(_spillStore.ReadAll());
        operations.AddRange(_intentLog);
        operations.Sort(static (left, right) => left.Ordinal.CompareTo(right.Ordinal));
        return operations;
    }

    private void Finish()
    {
        long bytes;
        lock (_gate)
        {
            if (_state == 2)
            {
                return;
            }

            _state = 2;
            bytes = checked(_residentIntentBytes + _assertionBytes);
            _residentIntentBytes = 0;
            _assertionBytes = 0;
            _assertions.Clear();
            _intentLog.Clear();
        }

        if (bytes != 0)
        {
            _database.TransactionMemoryPool.Release(bytes);
        }

        _spillStore?.Dispose();
    }

    private void EnsureActive()
    {
        if (_state != 0)
        {
            throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                "The transaction has already completed.");
        }

        _database.EnsureOpen();
    }

    private void EnsureWritable()
    {
        EnsureActive();
        if (_mode == PantsTransactionMode.ReadOnly)
        {
            throw PantsException.InvalidArgument("A read-only transaction cannot stage mutations.");
        }
    }

    private static void ValidateTimeToLive(TimeSpan? timeToLive)
    {
        if (timeToLive is { } value &&
            (value < TimeSpan.Zero || value.Ticks % TimeSpan.TicksPerSecond != 0))
        {
            throw PantsException.InvalidArgument(
                "TTL must be null, zero, or a non-negative whole number of seconds.");
        }
    }

    private static DateTimeOffset? CalculateExpiration(
        DateTimeOffset commitTime,
        TimeSpan? timeToLive)
    {
        if (timeToLive is null)
        {
            return null;
        }

        long maximumDeltaTicks = DateTimeOffset.MaxValue.UtcTicks - commitTime.UtcTicks;
        return timeToLive.Value.Ticks > maximumDeltaTicks
            ? DateTimeOffset.MaxValue
            : commitTime.Add(timeToLive.Value);
    }

    private static bool IsInRange(byte[] key, byte[] startInclusive, byte[] endExclusive) =>
        ByteArrayComparer.Instance.Compare(key, startInclusive) >= 0 &&
        ByteArrayComparer.Instance.Compare(key, endExclusive) < 0;

}
