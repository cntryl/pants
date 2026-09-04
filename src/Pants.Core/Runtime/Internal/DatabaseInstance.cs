using System.Text;

namespace Cntryl.Pants.Runtime.Internal;

sealed class DatabaseInstance :
    IPantsDatabase,
    IPantsColumnFamilyCatalog,
    IPantsTransactionFactory,
    IPantsDatabaseMaintenance,
    IPantsDatabaseDiagnostics,
    IPantsPersistentStorage,
    IPantsCloudDatabase
{
    readonly object _handleOwner = new();
    readonly RuntimeComposition _runtime;
    readonly Lock _shutdownGate = new();
    int _lifecycleState;
    Task? _shutdownTask;

    DatabaseInstance(
        PantsOpenOptions options,
        TransactionMemoryPool transactionMemoryPool,
        IPantsClock clock,
        RuntimeTelemetry telemetry,
        RuntimeComposition runtime)
    {
        Options = options;
        TransactionMemoryPool = transactionMemoryPool;
        Clock = clock;
        Telemetry = telemetry;
        _runtime = runtime;
        DefaultFamily = CreateHandle(new ColumnFamilyIdentity(
            0,
            "default",
            RuntimeState.DefaultFamilyVersion));
        var isPersistent = options.Storage is not PantsStorageConfiguration.InMemory;
        var isCloudBacked = options.Storage is PantsStorageConfiguration.Cloud or
            PantsStorageConfiguration.SimulatedCloud;
        Capabilities = new PantsDatabaseCapabilities(
            isPersistent,
            isCloudBacked,
            Enum.GetValues<PantsDurability>().Where(_runtime.Coordinator.IsSupported));
        ColumnFamilies = this;
        Transactions = this;
        Maintenance = this;
        Diagnostics = this;
        PersistentStorage = isPersistent ? this : null;
        Cloud = isCloudBacked ? this : null;
    }

    internal IPantsClock Clock { get; }

    internal TransactionMemoryPool TransactionMemoryPool { get; }

    internal RuntimeTelemetry Telemetry { get; }

    internal long CoordinatorCommandsEnqueued => _runtime.Coordinator.CommandsEnqueued;

    public IPantsColumnFamily DefaultFamily { get; }

    public async ValueTask<IPantsColumnFamily> CreateAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        ValidateColumnFamilyName(name);
        var identity = await _runtime.Coordinator
            .CreateColumnFamilyAsync(name, cancellationToken)
            .ConfigureAwait(false);
        return CreateHandle(identity);
    }

    public async ValueTask<IPantsColumnFamily?> GetAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(name);
        var identity = await _runtime.Coordinator
            .GetActiveColumnFamilyIdentityAsync(name, cancellationToken)
            .ConfigureAwait(false);
        return identity is { } value
            ? CreateHandle(value)
            : null;
    }

    public async ValueTask<IReadOnlyList<IPantsColumnFamily>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        var identities = await _runtime.Coordinator
            .ListColumnFamiliesAsync(cancellationToken)
            .ConfigureAwait(false);
        return identities
            .OrderBy(static identity => identity.Id)
            .Select(CreateHandle)
            .ToArray();
    }

    public ValueTask DropAsync(
        IPantsColumnFamily columnFamily,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        var handle = ValidateDroppableColumnFamily(columnFamily);
        return _runtime.Coordinator.DropColumnFamilyAsync(handle.Identity, false, cancellationToken);
    }

    public ValueTask DropDiscardingUnflushedAsync(
        IPantsColumnFamily columnFamily,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        var handle = ValidateDroppableColumnFamily(columnFamily);
        return _runtime.Coordinator.DropColumnFamilyAsync(handle.Identity, true, cancellationToken);
    }

    public PantsOpenOptions Options { get; }

    public PantsDatabaseCapabilities Capabilities { get; }

    public IPantsColumnFamilyCatalog ColumnFamilies { get; }

    public IPantsTransactionFactory Transactions { get; }

    public IPantsDatabaseMaintenance Maintenance { get; }

    public IPantsDatabaseDiagnostics Diagnostics { get; }

    public IPantsPersistentStorage? PersistentStorage { get; }

    public IPantsCloudDatabase? Cloud { get; }

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

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _lifecycleState) == 2)
        {
            return;
        }

        await ShutdownAsync(Options.Runtime.ShutdownTimeout).ConfigureAwait(false);
    }

    public ValueTask<PantsRuntimeMetrics> GetRuntimeMetricsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _runtime.Coordinator.GetRuntimeMetricsAsync(cancellationToken);
    }

    public ValueTask<PantsReadAmplificationMetrics> GetReadAmplificationMetricsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _runtime.Coordinator.GetReadAmplificationMetricsAsync(cancellationToken);
    }

    public ValueTask<PantsReadPathDiagnostics> GetReadPathDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _runtime.Coordinator.GetReadPathDiagnosticsAsync(cancellationToken);
    }

    public ValueTask<PantsRecoveryMetrics> GetRecoveryMetricsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _runtime.Coordinator.GetRecoveryMetricsAsync(cancellationToken);
    }

    public ValueTask<PantsStorageLayout> GetStorageLayoutAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _runtime.Coordinator.GetStorageLayoutAsync(cancellationToken);
    }

    public ValueTask FlushAsync(
        IPantsColumnFamily columnFamily,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        var handle = ValidateColumnFamily(columnFamily);
        return _runtime.Coordinator.FlushAsync(handle.Identity, cancellationToken);
    }

    public ValueTask CompactAllAsync(CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _runtime.Coordinator.CompactAsync(cancellationToken);
    }

    public ValueTask SetBackgroundCompactionAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _runtime.Coordinator.SetBackgroundCompactionAsync(enabled, cancellationToken);
    }

    public ValueTask<bool> WaitForWriteStallClearAsync(
        IPantsColumnFamily columnFamily,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        var handle = ValidateColumnFamily(columnFamily);
        return _runtime.Coordinator.WaitForWriteStallClearAsync(
            handle.Identity,
            timeout,
            cancellationToken);
    }

    public bool IsPrimaryLeaseHealthy => _runtime.Coordinator.IsPrimaryLeaseHealthy;

    public ValueTask<PantsStorageVerificationReport> VerifyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        return _runtime.Coordinator.VerifyStorageAsync(timeout, cancellationToken);
    }

    public async ValueTask<IPantsTransaction> BeginAsync(
        IPantsColumnFamily columnFamily,
        PantsTransactionMode mode,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        var handle = ValidateColumnFamily(columnFamily);
        if (!Enum.IsDefined(mode))
        {
            throw PantsException.InvalidArgument("Transaction mode is invalid.");
        }

        using var activity = PantsDiagnostics.ActivitySource.StartActivity(
            "PantsDatabase.BeginTransaction");
        activity?.SetTag("pants.column_family.id", handle.Id);
        activity?.SetTag("pants.transaction.mode", mode.ToString());
        if (mode == PantsTransactionMode.ReadOnly)
        {
            return _runtime.Coordinator.BeginReadOnlyTransaction(this, handle, cancellationToken);
        }

        return await _runtime.Coordinator.BeginTransactionAsync(this, handle, mode, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async ValueTask<DatabaseInstance> OpenAsync(
        PantsOpenOptions options,
        RuntimeDependencies dependencies,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dependencies);
        var plan = RuntimePlan.Resolve(options);
        var transactionMemoryPool = new TransactionMemoryPool(plan.TransactionMemoryPoolBytes);
        var clock = new MonotonicPantsClock(options.TtlClock);
        var telemetry = new RuntimeTelemetry(dependencies.RuntimeTimeProvider);
        var runtime = await RuntimeBootstrapper.OpenAsync(
                plan,
                clock,
                telemetry,
                dependencies,
                cancellationToken)
            .ConfigureAwait(false);
        return new DatabaseInstance(
            options,
            transactionMemoryPool,
            clock,
            telemetry,
            runtime);
    }

    async Task RunShutdownAttemptAsync(TaskCompletionSource completion)
    {
        try
        {
            await _runtime.Coordinator.ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
            await _runtime.DisposeAsync().ConfigureAwait(false);
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
                    if (exception is not PantsBusyException)
                    {
                        Volatile.Write(ref _lifecycleState, 0);
                    }
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
        TransactionInstance transaction,
        PantsWriteOptions options,
        CancellationToken cancellationToken)
    {
        EnsureOpen();
        return _runtime.Coordinator.CommitAsync(options, transaction.BuildCommitPayload(), cancellationToken);
    }

    internal ValueTask RollbackTransactionAsync(long transactionId, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _lifecycleState) == 2)
        {
            return ValueTask.CompletedTask;
        }

        return _runtime.Coordinator.RollbackAsync(transactionId, cancellationToken);
    }

    internal ValueTask RecordReadOnlyTransactionRollbackAsync(long transactionId) =>
        _runtime.Coordinator.RecordReadOnlyTransactionRollbackAsync(transactionId);

    internal ValueTask RecordReadOnlyTransactionCommitAsync(long transactionId) =>
        _runtime.Coordinator.RecordReadOnlyTransactionCommitAsync(transactionId);

    internal ValueTask<long> RegisterScanSnapshotAsync(
        DatabaseVersion snapshot,
        CancellationToken cancellationToken) =>
        _runtime.Coordinator.RegisterScanSnapshotAsync(snapshot, cancellationToken);

    internal ValueTask ReleaseScanSnapshotAsync(long snapshotId) =>
        _runtime.Coordinator.ReleaseScanSnapshotAsync(snapshotId, CancellationToken.None);

    internal ValueTask RecordPointReadAsync(
        ColumnFamilyIdentity columnFamily,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken) =>
        _runtime.Coordinator.RecordPointReadAsync(columnFamily, key, cancellationToken);

    internal ValueTask<SstEntry?> TryReadPointValueAsync(
        IReadOnlyList<FileMeta> candidatesNewestFirst,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken) =>
        _runtime.Coordinator.TryReadPointValueAsync(candidatesNewestFirst, key, cancellationToken);

    internal ValueTask<IReadOnlyList<AsyncSstScanSource>> CreateScanSourcesAsync(
        IReadOnlyList<FileMeta> candidates,
        PantsScanDirection direction,
        byte[]? startInclusive,
        byte[]? endExclusive,
        CancellationToken cancellationToken) =>
        _runtime.Coordinator.CreateScanSourcesAsync(
            candidates,
            direction,
            startInclusive,
            endExclusive,
            cancellationToken);

    internal ValueTask<PantsPointReadTrace> RecordPointReadWithDiagnosticsAsync(
        ColumnFamilyIdentity columnFamily,
        ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken) =>
        _runtime.Coordinator.RecordPointReadWithDiagnosticsAsync(columnFamily, key, cancellationToken);

    internal IScanReadValidator? CreateScanReadValidator(
        IReadOnlyList<AsyncSstScanSource> sources) =>
        _runtime.Coordinator.CreateScanReadValidator(sources);

    internal bool IsSupported(PantsDurability durability) => _runtime.Coordinator.IsSupported(durability);

    static void ValidateColumnFamilyName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var utf8Length = Encoding.UTF8.GetByteCount(name);
        if (utf8Length is 0 or > 255 || name.Contains('\0') || name == "default")
        {
            throw PantsException.InvalidArgument(
                "Column-family names must be 1-255 UTF-8 bytes, contain no NUL, and not be 'default'.");
        }
    }

    ColumnFamilyHandle ValidateColumnFamily(IPantsColumnFamily columnFamily)
    {
        ArgumentNullException.ThrowIfNull(columnFamily);
        if (columnFamily is not ColumnFamilyHandle handle || !handle.IsOwnedBy(_handleOwner))
        {
            throw PantsException.InvalidArgument(
                "The column-family handle was not created by this database instance.");
        }

        return handle;
    }

    ColumnFamilyHandle ValidateDroppableColumnFamily(IPantsColumnFamily columnFamily)
    {
        var handle = ValidateColumnFamily(columnFamily);
        if (handle.Id == 0)
        {
            throw PantsException.InvalidArgument("The default column family cannot be dropped.");
        }

        return handle;
    }

    ColumnFamilyHandle CreateHandle(ColumnFamilyIdentity identity) =>
        new(_handleOwner, identity.Id, identity.Name, identity.Generation);
}
