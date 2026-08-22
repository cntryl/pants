using System.Diagnostics;

namespace Pants;

internal sealed class PantsDatabaseInstance : IPantsDatabase
{
    private readonly PantsActor _actor;
    private readonly object _handleOwner = new();
    private readonly TransactionMemoryPool _transactionMemoryPool;
    private readonly IPantsClock _ttlClock;
    private readonly RuntimeTelemetry _telemetry;
    readonly Lock _shutdownGate = new();
    Task? _shutdownTask;
    private int _lifecycleState;

    internal PantsDatabaseInstance(PantsOpenOptions options, PantsRuntimeDependencies dependencies)
    {
        Options = options;
        _transactionMemoryPool = new TransactionMemoryPool(options.TransactionMemoryPoolBytes);
        _ttlClock = new MonotonicPantsClock(options.TtlClock);
        _telemetry = new RuntimeTelemetry();
        _actor = new PantsActor(options, _ttlClock, _telemetry, dependencies);
        DefaultColumnFamily = CreateHandle(new ColumnFamilyIdentity(
            0,
            "default",
            PantsRuntimeState.DefaultFamilyVersion));
    }

    public PantsOpenOptions Options { get; }

    public IPantsColumnFamily DefaultColumnFamily { get; }

    public async ValueTask<IPantsColumnFamily> CreateColumnFamilyAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        ValidateColumnFamilyName(name);
        ColumnFamilyIdentity identity = await _actor
            .CreateColumnFamilyAsync(name, cancellationToken)
            .ConfigureAwait(false);
        return CreateHandle(identity);
    }

    public async ValueTask<IPantsColumnFamily?> GetColumnFamilyAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(name);
        ColumnFamilyIdentity? identity = await _actor
            .GetActiveColumnFamilyIdentityAsync(name, cancellationToken)
            .ConfigureAwait(false);
        return identity is { } value
            ? CreateHandle(value)
            : null;
    }

    public async ValueTask<IReadOnlyList<IPantsColumnFamily>> ListColumnFamiliesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        IReadOnlyList<ColumnFamilyIdentity> identities = await _actor
            .ListColumnFamiliesAsync(cancellationToken)
            .ConfigureAwait(false);
        return identities
            .OrderBy(static identity => identity.Id)
            .Select(CreateHandle)
            .ToArray();
    }

    public ValueTask DropColumnFamilyAsync(
        IPantsColumnFamily columnFamily,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        PantsColumnFamilyHandle handle = ValidateDroppableColumnFamily(columnFamily);
        return _actor.DropColumnFamilyAsync(handle.Identity, discardUnflushed: false, cancellationToken);
    }

    public ValueTask DropColumnFamilyDiscardingUnflushedAsync(
        IPantsColumnFamily columnFamily,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        PantsColumnFamilyHandle handle = ValidateDroppableColumnFamily(columnFamily);
        return _actor.DropColumnFamilyAsync(handle.Identity, discardUnflushed: true, cancellationToken);
    }

    public async ValueTask<IPantsTransaction> BeginTransactionAsync(
        IPantsColumnFamily columnFamily,
        PantsTransactionMode mode,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        PantsColumnFamilyHandle handle = ValidateColumnFamily(columnFamily);
        if (!Enum.IsDefined(mode))
        {
            throw PantsException.InvalidArgument("Transaction mode is invalid.");
        }

        using Activity? activity = PantsDiagnostics.ActivitySource.StartActivity(
            "PantsDatabase.BeginTransaction");
        activity?.SetTag("pants.column_family.id", handle.Id);
        activity?.SetTag("pants.transaction.mode", mode.ToString());
        return await _actor
            .BeginTransactionAsync(this, handle, mode, cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask FlushAsync(
        IPantsColumnFamily columnFamily,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        PantsColumnFamilyHandle handle = ValidateColumnFamily(columnFamily);
        return _actor.FlushAsync(handle.Identity, cancellationToken);
    }

    public ValueTask CompactAllAsync(CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _actor.CompactAsync(cancellationToken);
    }

    public ValueTask SetBackgroundCompactionAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _actor.SetBackgroundCompactionAsync(enabled, cancellationToken);
    }

    public ValueTask<bool> WaitForWriteStallClearAsync(
        IPantsColumnFamily columnFamily,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        PantsColumnFamilyHandle handle = ValidateColumnFamily(columnFamily);
        return _actor.WaitForWriteStallClearAsync(
            handle.Identity,
            timeout,
            cancellationToken);
    }

    public bool IsPrimaryLeaseHealthy => _actor.IsPrimaryLeaseHealthy;

    public ValueTask<PantsRuntimeMetrics> GetRuntimeMetricsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _actor.GetRuntimeMetricsAsync(cancellationToken);
    }

    public ValueTask<PantsReadAmplificationMetrics> GetReadAmplificationMetricsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _actor.GetReadAmplificationMetricsAsync(cancellationToken);
    }

    public ValueTask<PantsReadPathDiagnostics> GetReadPathDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _actor.GetReadPathDiagnosticsAsync(cancellationToken);
    }

    public ValueTask<PantsRecoveryMetrics> GetRecoveryMetricsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _actor.GetRecoveryMetricsAsync(cancellationToken);
    }

    public ValueTask<PantsStorageLayout> GetStorageLayoutAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _actor.GetStorageLayoutAsync(cancellationToken);
    }

    public ValueTask<PantsStorageVerificationReport> VerifyStorageAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _actor.VerifyStorageAsync(timeout, cancellationToken);
    }

    public async ValueTask ShutdownAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw PantsException.InvalidArgument("Shutdown timeout must be greater than zero.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource? attempt = null;
        Task shutdown;
        lock (_shutdownGate)
        {
            if (Volatile.Read(ref _lifecycleState) == 2)
            {
                return;
            }

            if (_shutdownTask is null)
            {
                attempt = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _shutdownTask = attempt.Task;
                Volatile.Write(ref _lifecycleState, 1);
            }

            shutdown = _shutdownTask;
        }

        if (attempt is not null)
        {
            _ = RunShutdownAttemptAsync(attempt);
            _ = ObserveShutdownAttemptAsync(shutdown);
        }

        using var activity = PantsDiagnostics.ActivitySource.StartActivity("PantsDatabase.Shutdown");
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        try
        {
            await shutdown.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw PantsException.Create(
                PantsErrorCode.Timeout,
                "Database shutdown did not complete before its deadline.");
        }
    }

    async Task RunShutdownAttemptAsync(TaskCompletionSource completion)
    {
        try
        {
            await _actor.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
            await _actor.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _lifecycleState, 2);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            lock (_shutdownGate)
            {
                if (ReferenceEquals(_shutdownTask, completion.Task))
                {
                    _shutdownTask = null;
                }
            }

            completion.TrySetException(exception);
        }
    }

    static async Task ObserveShutdownAttemptAsync(Task shutdown)
    {
        try
        {
            await shutdown.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Callers may leave before the shared shutdown attempt terminates.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _lifecycleState) == 2)
        {
            return;
        }

        await ShutdownAsync(Options.ShutdownTimeout).ConfigureAwait(false);
    }

    internal IPantsClock Clock => _ttlClock;

    internal TransactionMemoryPool TransactionMemoryPool => _transactionMemoryPool;

    internal RuntimeTelemetry Telemetry => _telemetry;

    internal void EnsureOpen()
    {
        var state = Volatile.Read(ref _lifecycleState);
        if (state != 0)
        {
            throw state == 1
                ? new PantsBusyException("Pants database is shutting down.")
                : new PantsAbortedException("Pants database is disposed.");
        }
    }

    internal ValueTask CommitTransactionAsync(
        PantsTransactionInstance transaction,
        PantsWriteOptions options,
        CancellationToken cancellationToken)
    {
        EnsureOpen();
        return _actor.CommitAsync(options, transaction.BuildCommitPayload(), cancellationToken);
    }

    internal ValueTask RollbackTransactionAsync(long transactionId, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _lifecycleState) == 2)
        {
            return ValueTask.CompletedTask;
        }

        return _actor.RollbackAsync(transactionId, cancellationToken);
    }

    internal ValueTask<long> RegisterScanSnapshotAsync(
        DatabaseSnapshot snapshot,
        CancellationToken cancellationToken) =>
        _actor.RegisterScanSnapshotAsync(snapshot, cancellationToken);

    internal ValueTask ReleaseScanSnapshotAsync(long snapshotId) =>
        _actor.ReleaseScanSnapshotAsync(snapshotId, CancellationToken.None);

    internal ValueTask RecordPointReadAsync(
        ColumnFamilyIdentity columnFamily,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken) =>
        _actor.RecordPointReadAsync(columnFamily, key, cancellationToken);

    internal ValueTask<IScanReadValidator?> CreateScanReadValidatorAsync(
        ColumnFamilyIdentity columnFamily,
        PantsScanBounds bounds,
        CancellationToken cancellationToken) =>
        _actor.CreateScanReadValidatorAsync(columnFamily, bounds, cancellationToken);

    internal bool IsSupported(PantsDurability durability) => _actor.IsSupported(durability);

    private static void ValidateColumnFamilyName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int utf8Length = System.Text.Encoding.UTF8.GetByteCount(name);
        if (utf8Length is 0 or > 255 || name.Contains('\0') || name == "default")
        {
            throw PantsException.InvalidArgument(
                "Column-family names must be 1-255 UTF-8 bytes, contain no NUL, and not be 'default'.");
        }
    }

    private PantsColumnFamilyHandle ValidateColumnFamily(IPantsColumnFamily columnFamily)
    {
        ArgumentNullException.ThrowIfNull(columnFamily);
        if (columnFamily is not PantsColumnFamilyHandle handle || !handle.IsOwnedBy(_handleOwner))
        {
            throw PantsException.InvalidArgument(
                "The column-family handle was not created by this database instance.");
        }

        return handle;
    }

    private PantsColumnFamilyHandle ValidateDroppableColumnFamily(IPantsColumnFamily columnFamily)
    {
        PantsColumnFamilyHandle handle = ValidateColumnFamily(columnFamily);
        if (handle.Id == 0)
        {
            throw PantsException.InvalidArgument("The default column family cannot be dropped.");
        }

        return handle;
    }

    private PantsColumnFamilyHandle CreateHandle(ColumnFamilyIdentity identity) =>
        new(_handleOwner, identity.Id, identity.Name, identity.Generation);
}
