namespace Cntryl.Pants.Runtime.Internal;

sealed class RuntimeWorkerCommand
{
    readonly CancellationToken _callerCancellationToken;

    readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    readonly Func<CancellationToken, ValueTask> _operation;

    public RuntimeWorkerCommand(
        Func<CancellationToken, ValueTask> operation,
        CancellationToken callerCancellationToken)
    {
        _operation = operation;
        _callerCancellationToken = callerCancellationToken;
    }

    public Task Task => _completion.Task;

    public async ValueTask ExecuteAsync(CancellationToken lifetimeCancellationToken)
    {
        CancellationTokenSource? linkedCancellation = null;
        try
        {
            var executionCancellationToken = lifetimeCancellationToken;
            if (_callerCancellationToken.CanBeCanceled)
            {
                if (!lifetimeCancellationToken.CanBeCanceled ||
                    lifetimeCancellationToken == _callerCancellationToken)
                {
                    executionCancellationToken = _callerCancellationToken;
                }
                else
                {
                    linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        lifetimeCancellationToken,
                        _callerCancellationToken);
                    executionCancellationToken = linkedCancellation.Token;
                }
            }

            await _operation(executionCancellationToken).ConfigureAwait(false);
            _completion.TrySetResult();
        }
        catch (OperationCanceledException exception)
        {
            _completion.TrySetCanceled(
                _callerCancellationToken.IsCancellationRequested
                    ? _callerCancellationToken
                    : exception.CancellationToken);
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
        finally
        {
            linkedCancellation?.Dispose();
        }
    }
}
