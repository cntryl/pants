namespace Cntryl.Pants;

public static class PantsDatabase
{
    public static async ValueTask<IPantsDatabase> OpenAsync(
        PantsOpenOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        return await DatabaseInstance.OpenAsync(
                options,
                RuntimeDependencies.Default,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async ValueTask<IPantsDatabase> OpenForTestingAsync(
        PantsOpenOptions options,
        RuntimeDependencies dependencies,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dependencies);
        cancellationToken.ThrowIfCancellationRequested();
        return await DatabaseInstance.OpenAsync(options, dependencies, cancellationToken)
            .ConfigureAwait(false);
    }

    public static ValueTask<PantsStorageVerificationReport> VerifyPathAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        StorageVerifier.VerifyPathAsync(path, cancellationToken);
}
