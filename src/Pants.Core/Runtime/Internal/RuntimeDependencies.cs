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
        long? hybridLocalStorageBudgetBytes = null)
    {
        Failpoints = failpoints ?? NullPantsFailpointHandler.Instance;
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

    public IFailpointHandler Failpoints { get; }

    public StorageVerificationDelegate StorageVerifier { get; }

    public TimeSpan? LeaseHeartbeatInterval { get; }

    public HttpClient? CloudHttpClient { get; }

    public VerificationBarrierResponseDelegate VerificationBarrierResponse { get; }

    public TimeProvider RuntimeTimeProvider { get; }

    public IPantsClock LeaseClock { get; }

    public long? HybridLocalStorageBudgetBytes { get; }

    public StartupPhaseRecorder StartupPhases { get; }

    public static RuntimeDependencies Default { get; } = new();

    static ValueTask NoopVerificationBarrierResponse() => ValueTask.CompletedTask;
}
