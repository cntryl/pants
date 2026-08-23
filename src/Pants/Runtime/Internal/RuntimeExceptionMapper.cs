namespace Cntryl.Pants.Runtime.Internal;

static class RuntimeExceptionMapper
{
    public static Exception ToPublicException(
        Exception exception,
        CancellationToken callerCancellationToken = default) => exception switch
        {
            OperationCanceledException canceled when
                callerCancellationToken.IsCancellationRequested &&
                canceled.CancellationToken == callerCancellationToken => canceled,
            OperationCanceledException canceled => new PantsAbortedException(
                "The Pants runtime operation was aborted by the engine.",
                canceled),
            WalCommitRollbackException or
                WalCommitGroupRollbackException or
                WalRotationRecoveryException =>
                new PantsAbortedException(
                    "The WAL outcome is uncertain after a failed rollback.",
                    exception),
            PantsException => exception,
            IOException ioException => PantsException.FromIOException(ioException),
            UnauthorizedAccessException accessException => new PantsIOException(
                accessException.Message,
                accessException),
            _ => new PantsInternalException(
                "An unexpected runtime failure occurred.",
                exception)
        };

    public static bool IsNoSpace(Exception exception)
    {
        if (exception is PantsNoSpaceException)
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Any(IsNoSpace);
        }

        return exception.InnerException is { } innerException && IsNoSpace(innerException);
    }
}
