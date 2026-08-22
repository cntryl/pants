namespace Pants;

internal sealed class PantsRuntimeDependencies
{
    public PantsRuntimeDependencies(
        IPantsFailpointHandler? failpoints = null,
        PantsStorageVerificationDelegate? storageVerifier = null,
        TimeSpan? leaseHeartbeatInterval = null,
        HttpClient? cloudHttpClient = null)
    {
        Failpoints = failpoints ?? NullPantsFailpointHandler.Instance;
        StorageVerifier = storageVerifier ?? PantsStorageVerifier.VerifyPathAsync;
        LeaseHeartbeatInterval = leaseHeartbeatInterval ?? TimeSpan.FromSeconds(10);
        CloudHttpClient = cloudHttpClient;
        if (LeaseHeartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseHeartbeatInterval),
                "The lease heartbeat interval must be greater than zero.");
        }
    }

    public IPantsFailpointHandler Failpoints { get; }

    public PantsStorageVerificationDelegate StorageVerifier { get; }

    public TimeSpan LeaseHeartbeatInterval { get; }

    public HttpClient? CloudHttpClient { get; }

    public static PantsRuntimeDependencies Default { get; } = new();
}
