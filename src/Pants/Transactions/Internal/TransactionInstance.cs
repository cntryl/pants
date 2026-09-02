namespace Cntryl.Pants.Transactions.Internal;

sealed class TransactionInstance : IPantsTransaction
{
    const int OperationAccountingOverhead = 64;
    const int AssertionAccountingOverhead = 48;
    readonly List<TransactionAssertion> _assertions = [];
    readonly ColumnFamilyHandle _columnFamily;

    readonly DatabaseInstance _database;
    readonly object _gate = new();
    readonly List<TransactionIntentOperation> _intentLog = [];
    readonly DateTimeOffset _snapshotTime;
    readonly TransactionSpillStore? _spillStore;
    readonly DatabaseVersion _startSnapshot;
    readonly long _transactionId;
    readonly bool _coordinatorRegistered;
    long _assertionBytes;
    PantsConflictPolicy _conflictPolicy = PantsConflictPolicy.LastWriteWins;
    ulong _nextOrdinal;
    long _residentIntentBytes;
    int _state;

    internal TransactionInstance(
        DatabaseInstance database,
        long transactionId,
        ColumnFamilyHandle columnFamily,
        PantsTransactionMode mode,
        DatabaseVersion startSnapshot,
        DateTimeOffset snapshotTime,
        string? persistentDatabasePath,
        bool coordinatorRegistered = true)
    {
        _database = database;
        _transactionId = transactionId;
        _columnFamily = columnFamily;
        Mode = mode;
        _startSnapshot = startSnapshot;
        _snapshotTime = snapshotTime;
        _coordinatorRegistered = coordinatorRegistered;
        _spillStore = mode == PantsTransactionMode.ReadOnly || persistentDatabasePath is null
            ? null
            : new TransactionSpillStore(persistentDatabasePath, transactionId, columnFamily.Identity);
    }

    public IPantsColumnFamily ColumnFamily => _columnFamily;

    public PantsTransactionMode Mode { get; }

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
        StagePointWrite(key, value, timeToLive, false);

    public void Insert(
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value,
        TimeSpan? timeToLive = null) =>
        StagePointWrite(key, value, timeToLive, true);

    public void Delete(ReadOnlyMemory<byte> key)
    {
        lock (_gate)
        {
            EnsureWritable();
            var keyCopy = key.ToArray();
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
            var startCopy = startInclusive.ToArray();
            var endCopy = endExclusive.ToArray();
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
            var keyCopy = key.ToArray();
            var valueCopy = expectedValue?.ToArray();
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

    public async ValueTask<ReadOnlyMemory<byte>?> GetAsync(
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken = default)
    {
        var (keyCopy, value, resolvedByIntent) = PreparePointRead(key, cancellationToken);

        // Unconditional even when ReadVisibleValue already resolved the value from an SST:
        // this exhaustive pass is the sole source of read-amplification telemetry and the
        // signal that triggers background read-amplification-driven compaction, independent of
        // this early-exit real resolution.
        if (!resolvedByIntent)
        {
            await _database.RecordPointReadAsync(
                _columnFamily.Identity,
                keyCopy,
                cancellationToken).ConfigureAwait(false);
        }

        return CopyValue(value);
    }

    public async ValueTask<PantsPointReadResult> GetWithDiagnosticsAsync(
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken = default)
    {
        var (keyCopy, value, resolvedByIntent) = PreparePointRead(key, cancellationToken);
        if (resolvedByIntent)
        {
            return new PantsPointReadResult(
                CopyValue(value),
                new PantsPointReadTrace(0, []));
        }

        var trace = await _database.RecordPointReadWithDiagnosticsAsync(
            _columnFamily.Identity,
            keyCopy,
            cancellationToken).ConfigureAwait(false);
        return new PantsPointReadResult(CopyValue(value), trace);
    }

    public async ValueTask<IPantsScan> ScanAsync(
        PantsScanQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var bounds = new ScanBounds(query);
        TransactionIntentReadView intents;
        lock (_gate)
        {
            EnsureActive();
            _database.EnsureOpen();
            intents = CreateIntentReadView();
        }

        long snapshotId;
        try
        {
            snapshotId = await _database
                .RegisterScanSnapshotAsync(_startSnapshot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            intents.Dispose();
            throw;
        }

        try
        {
            return new ScanInstance(
                async scanCancellationToken =>
                {
                    var validator = await _database.CreateScanReadValidatorAsync(
                        _columnFamily.Identity,
                        bounds,
                        scanCancellationToken).ConfigureAwait(false);
                    TransactionScanEnumerator? entries = null;
                    try
                    {
                        var candidates = SnapshotReadPath.ResolveCandidateFilesForRange(
                            _startSnapshot,
                            _columnFamily.Identity,
                            bounds.StartInclusive,
                            bounds.EndExclusive);
                        var sstSources = candidates.Count == 0
                            ? []
                            : _database.CreateScanSources(
                                candidates,
                                bounds.Direction,
                                bounds.StartInclusive,
                                bounds.EndExclusive);
                        entries = new TransactionScanEnumerator(
                            _startSnapshot,
                            _columnFamily.Identity,
                            _snapshotTime,
                            intents,
                            bounds,
                            sstSources);
                        return validator is null
                            ? entries
                            : new ValidatingScanEnumerator(entries, validator);
                    }
                    catch
                    {
                        entries?.Dispose();
                        validator?.Dispose();
                        throw;
                    }
                },
                query,
                ReleaseScanAsync);
        }
        catch
        {
            intents.Dispose();
            await _database.ReleaseScanSnapshotAsync(snapshotId).ConfigureAwait(false);
            throw;
        }

        async ValueTask ReleaseScanAsync()
        {
            try
            {
                await _database.ReleaseScanSnapshotAsync(snapshotId).ConfigureAwait(false);
            }
            finally
            {
                intents.Dispose();
            }
        }
    }

    public async ValueTask CommitAsync(
        PantsWriteOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (Mode != PantsTransactionMode.ReadOnly &&
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

        using var activity = PantsDiagnostics.ActivitySource.StartActivity("PantsTransaction.Commit");
        try
        {
            if (_coordinatorRegistered)
            {
                await _database.CommitTransactionAsync(this, options, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _database.RecordReadOnlyTransactionCommitAsync(_transactionId)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            if (_coordinatorRegistered)
            {
                await _database.RollbackTransactionAsync(_transactionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                await _database.RecordReadOnlyTransactionRollbackAsync(_transactionId)
                    .ConfigureAwait(false);
            }
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

        using var activity = PantsDiagnostics.ActivitySource.StartActivity("PantsTransaction.Rollback");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_coordinatorRegistered)
            {
                await _database.RollbackTransactionAsync(_transactionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _database.RecordReadOnlyTransactionRollbackAsync(_transactionId)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            if (_coordinatorRegistered)
            {
                await _database.RollbackTransactionAsync(_transactionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                await _database.RecordReadOnlyTransactionRollbackAsync(_transactionId)
                    .ConfigureAwait(false);
            }
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

            var operations = new TransactionOperationSource(
                _spillStore,
                _intentLog,
                _nextOrdinal,
                _database.Clock.UtcNow);
            operations.Validate();
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
                Mode,
                _conflictPolicy,
                _snapshotTime,
                _startSnapshot,
                operations,
                assertions);
        }
    }

    void StagePointWrite(
        ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value,
        TimeSpan? timeToLive,
        bool insertOnly)
    {
        lock (_gate)
        {
            EnsureWritable();
            ValidateTimeToLive(timeToLive);
            var keyCopy = key.ToArray();
            var valueCopy = value.ToArray();
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

    (byte[]? Value, bool ResolvedByIntent) ReadVisibleValue(byte[] key)
    {
        using var intents = CreateIntentReadView();
        var lookup = intents.LookupLatest(key);
        if (lookup is not null)
        {
            return (lookup.IsDeleted ? null : lookup.Value?.ToArray(), true);
        }

        if (_startSnapshot.Families.TryGetValue(_columnFamily.Identity, out var family) &&
            family.TryGetValue(key, out var cell))
        {
            return cell.Value is null || cell.IsExpired(_snapshotTime)
                ? (null, false)
                : (cell.Value.ToArray(), false);
        }

        // Absent from the in-memory tier: it was either never written, or written and later
        // released from RuntimeState.FamilyData once its flush durably published (see
        // RuntimeState.ReleaseFlushedGeneration) — either way, the SST is now the sole source.
        // Candidates come from this transaction's own pinned snapshot, not the live manifest, so
        // a concurrent compaction/flush cannot change what this snapshot sees.
        var candidates = SnapshotReadPath.ResolveCandidateFilesForPoint(
            _startSnapshot,
            _columnFamily.Identity,
            key);
        var entry = candidates.Count == 0 ? null : _database.TryReadPointValue(candidates, key);
        var coveringRangeSequence = _startSnapshot.RangeTombstones[_columnFamily.Identity]
            .Where(tombstone =>
                ByteArrayComparer.Instance.Compare(key, tombstone.Start) >= 0 &&
                ByteArrayComparer.Instance.Compare(key, tombstone.EndExclusive) < 0)
            .Select(static tombstone => (long?)tombstone.WriteSequence)
            .Max();
        if (coveringRangeSequence is { } rangeSequence &&
            (entry is null || rangeSequence > checked((long)entry.Sequence)))
        {
            return (null, false);
        }

        if (entry is null || entry.IsDelete || UnixTimestamp.IsExpired(entry.Expiration, _snapshotTime))
        {
            return (null, false);
        }

        return (entry.Value?.ToArray(), false);
    }

    (byte[] Key, byte[]? Value, bool ResolvedByIntent) PreparePointRead(
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EnsureActive();
            _database.EnsureOpen();
            var keyCopy = key.ToArray();
            var (value, resolvedByIntent) = ReadVisibleValue(keyCopy);
            return (keyCopy, value, resolvedByIntent);
        }
    }

    static ReadOnlyMemory<byte>? CopyValue(byte[]? value) =>
        value is null
            ? (ReadOnlyMemory<byte>?)null
            : new ReadOnlyMemory<byte>(value.ToArray());

    TransactionIntentReadView CreateIntentReadView() =>
        _spillStore?.CreateReadView(_intentLog) ?? new TransactionIntentReadView(_intentLog);

    void StageIntent(TransactionIntentOperation operation, long bytes)
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
            SpillResidentIntents();
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

    void ReserveAssertion(long bytes)
    {
        if (_database.TransactionMemoryPool.TryReserve(bytes))
        {
            _assertionBytes = checked(_assertionBytes + bytes);
            return;
        }

        if (_spillStore is not null && _intentLog.Count != 0)
        {
            SpillResidentIntents();
        }

        if (!_database.TransactionMemoryPool.TryReserve(bytes))
        {
            throw PantsException.Create(
                PantsErrorCode.ResourceLimit,
                $"The transaction memory pool cannot admit {bytes} assertion bytes.");
        }

        _assertionBytes = checked(_assertionBytes + bytes);
    }

    void SpillResidentIntents()
    {
        var spillStore = _spillStore ??
                         throw new PantsInternalException("A transaction spill store is unavailable.");
        spillStore.WriteRun(_intentLog);
        _intentLog.Clear();
        _database.TransactionMemoryPool.Release(_residentIntentBytes);
        _residentIntentBytes = 0;
    }

    void Finish()
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

    void EnsureActive()
    {
        if (_state != 0)
        {
            throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                "The transaction has already completed.");
        }

        _database.EnsureOpen();
    }

    void EnsureWritable()
    {
        EnsureActive();
        if (Mode == PantsTransactionMode.ReadOnly)
        {
            throw PantsException.InvalidArgument("A read-only transaction cannot stage mutations.");
        }
    }

    static void ValidateTimeToLive(TimeSpan? timeToLive)
    {
        if (timeToLive is { } value &&
            (value < TimeSpan.Zero || value.Ticks % TimeSpan.TicksPerSecond != 0))
        {
            throw PantsException.InvalidArgument(
                "TTL must be null, zero, or a non-negative whole number of seconds.");
        }
    }
}
