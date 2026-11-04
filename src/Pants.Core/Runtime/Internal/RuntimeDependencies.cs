namespace Cntryl.Pants.Runtime.Internal;

sealed class RuntimeDependencies
{
    public RuntimeDependencies(
        IFailpointHandler? failpoints = null,
        StorageVerificationDelegate? storageVerifier = null,
        TimeSpan? leaseHeartbeatInterval = null,
        HttpClient? cloudHttpClient = null,
        VerificationBarrierResponseDelegate? verificationBarrierResponse = null,
        TimeProvider? runtimeTimeProvider = null,
        Action<StartupPhaseMeasurement>? startupPhaseMeasurement = null,
        IPantsClock? leaseClock = null,
        long? hybridLocalStorageBudgetBytes = null,
        TimeSpan? workerDisposalTimeout = null,
        ulong? initialMemorySequence = null,
        TimeSpan? cloudWalRetirementQuantum = null,
        Action<RecoveryWorkCompleted>? recoveryWorkCompleted = null,
        Action<StartupEvent>? startupObserver = null,
        Action<long>? commitAdmittedObserver = null)
    {
        RecoveryWorkCompleted = recoveryWorkCompleted;
        StartupObserver = startupObserver;
        CommitAdmittedObserver = commitAdmittedObserver;
        InitialMemorySequence = initialMemorySequence;
        CloudWalRetirementQuantum = cloudWalRetirementQuantum;
        WorkerDisposalTimeout = workerDisposalTimeout;
        Failpoints = failpoints ?? NullPantsFailpointHandler.Instance;
        UsesDefaultStorageVerifier = storageVerifier is null;
        StorageVerifier = storageVerifier ?? Storage.Internal.StorageVerifier.VerifyPathAsync;
        LeaseHeartbeatInterval = leaseHeartbeatInterval;
        CloudHttpClient = cloudHttpClient;
        VerificationBarrierResponse = verificationBarrierResponse ?? NoopVerificationBarrierResponse;
        RuntimeTimeProvider = runtimeTimeProvider ?? TimeProvider.System;
        LeaseClock = leaseClock ?? SystemPantsClock.Instance;
        HybridLocalStorageBudgetBytes = hybridLocalStorageBudgetBytes;
        StartupPhases = new StartupPhaseRecorder(startupPhaseMeasurement);
        if (LeaseHeartbeatInterval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseHeartbeatInterval),
                "The lease heartbeat interval must be greater than zero.");
        }

        if (HybridLocalStorageBudgetBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hybridLocalStorageBudgetBytes),
                "The hybrid local storage budget must be greater than zero.");
        }
    }

    /// <summary>Seeds the in-memory runtime sequence so tests can reach sequence-space exhaustion.</summary>
    public ulong? InitialMemorySequence { get; }

    public IFailpointHandler Failpoints { get; }

    /// <summary>How long one background cloud WAL retirement turn works before yielding.</summary>
    public TimeSpan? CloudWalRetirementQuantum { get; }

    public StorageVerificationDelegate StorageVerifier { get; }

    public bool UsesDefaultStorageVerifier { get; }

    public TimeSpan? LeaseHeartbeatInterval { get; }

    public HttpClient? CloudHttpClient { get; }

    public VerificationBarrierResponseDelegate VerificationBarrierResponse { get; }

    public TimeProvider RuntimeTimeProvider { get; }

    /// <summary>How long disposal waits for a background worker loop before deferring release.</summary>
    public TimeSpan? WorkerDisposalTimeout { get; }

    public IPantsClock LeaseClock { get; }

    public long? HybridLocalStorageBudgetBytes { get; }

    public StartupPhaseRecorder StartupPhases { get; }

    public Action<RecoveryWorkCompleted>? RecoveryWorkCompleted { get; }

    public Action<StartupEvent>? StartupObserver { get; }

    public Action<long>? CommitAdmittedObserver { get; }

    public void ObserveStartup(StartupEvent startupEvent)
    {
        try
        {
            StartupObserver?.Invoke(startupEvent);
        }
        catch (Exception)
        {
            // Diagnostic observers cannot interrupt ownership transfer or resource cleanup.
        }
    }

    public static RuntimeDependencies Default { get; } = new();

    static ValueTask NoopVerificationBarrierResponse() => ValueTask.CompletedTask;
}
