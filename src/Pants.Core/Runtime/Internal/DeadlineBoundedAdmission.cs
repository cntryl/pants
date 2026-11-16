using System.Threading.Channels;

namespace Cntryl.Pants.Runtime.Internal;

/// <summary>
///     Offers one command to the bounded runtime queue without waiting past the operation's
///     deadline. Expiry here is a definite outcome: the command was never handed to the runtime.
/// </summary>
static class DeadlineBoundedAdmission
{
    /// <returns>
    ///     <see langword="true" /> once the queue accepted the command; <see langword="false" /> when
    ///     the deadline expired first and the command was not enqueued.
    /// </returns>
    public static async ValueTask<bool> TryWriteAsync<T>(
        ChannelWriter<T> writer,
        T command,
        OperationDeadline deadline,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (writer.TryWrite(command))
        {
            return true;
        }

        if (!deadline.IsBounded)
        {
            await writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
            return true;
        }

        using var expiry = new OperationDeadlineCancellation(deadline, timeProvider, cancellationToken);
        try
        {
            await writer.WriteAsync(command, expiry.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            // Callers match cancellation against their own token, not the linked expiry token.
            throw new OperationCanceledException(exception.Message, exception, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
