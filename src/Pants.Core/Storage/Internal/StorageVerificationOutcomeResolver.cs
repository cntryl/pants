namespace Cntryl.Pants.Storage.Internal;

/// <summary>
///     Runs offline path verification and classifies every result, including failures, into a
///     <see cref="PantsStorageVerificationOutcome" />. Only cancellation escapes as an exception.
/// </summary>
static class StorageVerificationOutcomeResolver
{
    public static async ValueTask<PantsStorageVerificationOutcome> VerifyPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (StoragePathPreflight.Check(path) is { } pathFailure)
        {
            return pathFailure;
        }

        try
        {
            var report = await StorageVerifier.VerifyPathAsync(path, cancellationToken).ConfigureAwait(false);
            return PantsStorageVerificationOutcome.FromReport(report);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PantsException exception)
        {
            return PantsStorageVerificationOutcome.FromException(exception);
        }
        catch (IOException exception)
        {
            return PantsStorageVerificationOutcome.FromException(PantsException.FromIOException(exception));
        }
        catch (UnauthorizedAccessException exception)
        {
            return PantsStorageVerificationOutcome.FromException(
                PantsException.Create(PantsErrorCode.Io, exception.Message, exception));
        }
        catch (Exception exception)
        {
            return PantsStorageVerificationOutcome.FromException(
                PantsException.Create(PantsErrorCode.Internal, exception.Message, exception));
        }
    }
}
