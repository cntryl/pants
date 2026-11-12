namespace Cntryl.Pants.Storage.Internal.Sst;

/// <summary>One runtime owner for content and retained reader/open metadata.</summary>
sealed class SstReadResources : IDisposable
{
    readonly long _coordinationLimit;
    readonly object _gate = new();
    readonly int _maximumOpenOwners;
    TaskCompletionSource _capacityChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long _coordinationBytes;
    int _openOwners;
    bool _disposed;

    public SstReadResources(long allowanceBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(allowanceBytes);
        MetadataBudget = new ResourceBudget(allowanceBytes / 4);
        ContentCapacityBytes = allowanceBytes - MetadataBudget.Limit;
        HandleBudget = new SstReaderHandleBudget(SstReaderHandleBudget.DefaultLimit);
        _maximumOpenOwners = (int)Math.Clamp(MetadataBudget.Limit / 8192, 1, 8);
        _coordinationLimit = MetadataBudget.Limit / 8;
    }

    public long ContentCapacityBytes { get; }
    public ResourceBudget MetadataBudget { get; }
    public SstReaderHandleBudget HandleBudget { get; }

    public IDisposable EnterOpen(string name)
    {
        var bytes = CoordinationBytes(name);
        while (!TryEnter(bytes, out var capacity))
        {
            capacity.GetAwaiter().GetResult();
        }
        return ClaimOpen(bytes);
    }

    public async ValueTask<IDisposable> EnterOpenAsync(string name, CancellationToken cancellationToken)
    {
        var bytes = CoordinationBytes(name);
        while (!TryEnter(bytes, out var capacity))
        {
            await capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        return ClaimOpen(bytes);
    }

    // Compatibility reservation quantum from the pinned 64-bit Midge admission owner. This
    // models coordination allowance; it does not report CLR object sizes or process RSS.
    static long CoordinationBytes(string name) => checked(392L + name.Length * 2L);

    bool TryEnter(long bytes, out Task capacity)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (bytes > _coordinationLimit)
            {
                throw new PantsResourceLimitException(
                    $"SST open coordination requires {bytes} bytes but its allowance is {_coordinationLimit} bytes.");
            }
            capacity = _capacityChanged.Task;
            if (_openOwners >= _maximumOpenOwners || bytes > _coordinationLimit - _coordinationBytes)
            {
                return false;
            }
            _openOwners++;
            _coordinationBytes += bytes;
            return true;
        }
    }

    OpenLease ClaimOpen(long bytes)
    {
        try
        {
            return new OpenLease(this, bytes, MetadataBudget.Reserve(bytes));
        }
        catch
        {
            ReleaseOpen(bytes);
            throw;
        }
    }

    void ReleaseOpen(long bytes)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            _openOwners--;
            _coordinationBytes -= bytes;
            changed = _capacityChanged;
            _capacityChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
    }

    public void Dispose()
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            _disposed = true;
            changed = _capacityChanged;
        }
        changed.TrySetResult();
    }

    sealed class OpenLease(SstReadResources owner, long bytes, IDisposable metadataClaim) : IDisposable
    {
        IDisposable? _metadataClaim = metadataClaim;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _metadataClaim, null) is { } claim)
            {
                claim.Dispose();
                owner.ReleaseOpen(bytes);
            }
        }
    }
}
