namespace Cntryl.Pants.Runtime.Internal;

sealed class RuntimeResponseSlot<T>
{
    readonly Action<T>? _abandonedResponse;
    readonly TaskCompletionSource<T> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object _gate = new();
    readonly long _requestId;
    readonly RuntimeResponseRegistry _registry;
    int _state;

    public RuntimeResponseSlot(
        RuntimeResponseRegistry registry,
        long requestId,
        string requestKind,
        Action<T>? abandonedResponse = null)
    {
        _registry = registry;
        _requestId = requestId;
        _abandonedResponse = abandonedResponse;
        registry.Register(requestId, requestKind);
    }

    public Task<T> Response => _completion.Task;

    public void Complete(T response)
    {
        var deliver = false;
        var cleanUp = false;
        lock (_gate)
        {
            if (_state == 0)
            {
                _state = 1;
                _registry.Complete(_requestId);
                deliver = true;
            }
            else if (_state == 2)
            {
                _state = 3;
                _registry.CompleteLate(_requestId);
                cleanUp = true;
            }
        }

        if (deliver)
        {
            _completion.TrySetResult(response);
        }
        else if (cleanUp)
        {
            try
            {
                _abandonedResponse?.Invoke(response);
            }
            catch
            {
                // Cleanup of an unobserved result cannot restore response ownership.
            }
        }
    }

    public void Fail(Exception exception)
    {
        var deliver = false;
        lock (_gate)
        {
            if (_state == 0)
            {
                _state = 1;
                _registry.Complete(_requestId);
                deliver = true;
            }
            else if (_state == 2)
            {
                _state = 3;
                _registry.CompleteLate(_requestId);
            }
        }

        if (deliver)
        {
            _completion.TrySetException(exception);
        }
    }

    public bool Abandon(TimeSpan timeout)
    {
        lock (_gate)
        {
            if (_state != 0)
            {
                return false;
            }

            _state = 2;
            _registry.Abandon(_requestId, timeout);
            return true;
        }
    }

    public void Unregister()
    {
        lock (_gate)
        {
            if (_state == 0)
            {
                _state = 4;
                _registry.Cancel(_requestId);
            }
        }
    }
}
