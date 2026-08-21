namespace Pants;

public static class PantsDatabase
{
    public static async ValueTask<IPantsDatabase> OpenAsync(
        PantsOpenOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        // The current local-format implementation performs a bounded set of
        // synchronous filesystem calls. Keep those off an async caller's thread.
        return await Task.Run(
            () => new PantsDatabaseInstance(options),
            cancellationToken).ConfigureAwait(false);
    }

    public static ValueTask<PantsStorageVerificationReport> VerifyPathAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        PantsStorageVerifier.VerifyPathAsync(path, cancellationToken);
}
