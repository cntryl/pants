namespace Pants;

internal sealed class PantsRuntimeDependencies
{
    public PantsRuntimeDependencies(
        IPantsFailpointHandler? failpoints = null,
        PantsStorageVerificationDelegate? storageVerifier = null)
    {
        Failpoints = failpoints ?? NullPantsFailpointHandler.Instance;
        StorageVerifier = storageVerifier ?? PantsStorageVerifier.VerifyPathAsync;
    }

    public IPantsFailpointHandler Failpoints { get; }

    public PantsStorageVerificationDelegate StorageVerifier { get; }

    public static PantsRuntimeDependencies Default { get; } = new();
}
