using System.Collections.Concurrent;

namespace Cntryl.Pants.Runtime.Internal;

sealed class RuntimeResponseRegistry
{
    const int AbandonedRequestCapacity = 1024;
    static readonly TimeSpan AbandonedRequestRetention = TimeSpan.FromMinutes(5);
    readonly Dictionary<long, AbandonedRuntimeRequest> _abandoned = [];
    readonly Queue<long> _abandonedOrder = [];
    readonly object _gate = new();
    readonly ConcurrentDictionary<long, RuntimeRequestMetadata> _pending = new();
    readonly RuntimeTelemetry _telemetry;
    readonly TimeProvider _timeProvider;

    public RuntimeResponseRegistry(RuntimeTelemetry telemetry, TimeProvider timeProvider)
    {
        _telemetry = telemetry;
        _timeProvider = timeProvider;
    }

    public int PendingCount => _pending.Count;

    public int AbandonedMetadataCount
    {
        get
        {
            lock (_gate)
            {
                PurgeExpired();
                return _abandoned.Count;
            }
        }
    }

    public void Register(long requestId, string requestKind)
    {
        if (!_pending.TryAdd(
                requestId,
                new RuntimeRequestMetadata(requestKind, _timeProvider.GetUtcNow())))
        {
            throw new PantsInternalException($"Runtime request ID {requestId} was registered twice.");
        }
    }

    public void Complete(long requestId) => _pending.TryRemove(requestId, out _);

    public void Cancel(long requestId) => _pending.TryRemove(requestId, out _);

    public void Abandon(long requestId, TimeSpan timeout)
    {
        if (!_pending.TryRemove(requestId, out var pending))
        {
            return;
        }

        _telemetry.RecordRuntimeRequestAbandoned();
        lock (_gate)
        {
            PurgeExpired();
            _abandoned[requestId] = new AbandonedRuntimeRequest(
                pending.RequestKind,
                timeout,
                pending.RegisteredAt,
                AddSaturating(_timeProvider.GetUtcNow(), AbandonedRequestRetention));
            _abandonedOrder.Enqueue(requestId);
            while (_abandoned.Count > AbandonedRequestCapacity &&
                   _abandonedOrder.TryDequeue(out var evicted))
            {
                _abandoned.Remove(evicted);
            }
        }
    }

    public void CompleteLate(long requestId)
    {
        lock (_gate)
        {
            PurgeExpired();
            if (!_abandoned.Remove(requestId))
            {
                return;
            }
        }

        _telemetry.RecordRuntimeLateResponse();
    }

    void PurgeExpired()
    {
        var now = _timeProvider.GetUtcNow();
        while (_abandonedOrder.TryPeek(out var requestId))
        {
            if (!_abandoned.TryGetValue(requestId, out var request))
            {
                _abandonedOrder.Dequeue();
                continue;
            }

            if (request.ExpiresAt > now)
            {
                break;
            }

            _abandonedOrder.Dequeue();
            _abandoned.Remove(requestId);
        }
    }

    static DateTimeOffset AddSaturating(DateTimeOffset value, TimeSpan elapsed) =>
        value > DateTimeOffset.MaxValue - elapsed
            ? DateTimeOffset.MaxValue
            : value + elapsed;

    sealed record RuntimeRequestMetadata(string RequestKind, DateTimeOffset RegisteredAt);

    sealed record AbandonedRuntimeRequest(
        string RequestKind,
        TimeSpan Timeout,
        DateTimeOffset RegisteredAt,
        DateTimeOffset ExpiresAt);
}
