namespace Cntryl.Pants.Storage.Internal.Cache;

/// <summary>
///     Caches parsed async SST readers (local or remote) under a metadata byte budget so repeated
///     reads and scans stop re-fetching footers, indexes and blooms.
/// </summary>
/// <remarks>
///     Concurrent first opens of one SST share a single open. A failed open fails every waiter and
///     leaves nothing cached, so the next caller retries. Callers receive their own shared handle
///     and dispose it as before; evicting or removing a reader only drops the cache's own handle, so
///     a reader in use stays valid until its holders release it. Readers are keyed by manifest
///     identity, so a name reused for a different manifest entry never shares the old reader.
/// </remarks>
sealed class AsyncSstReaderCache : IDisposable
{
    readonly long _budgetBytes;
    readonly object _gate = new();
    readonly LinkedList<Slot> _recency = [];
    readonly Dictionary<SstFileIdentity, Slot> _slots = [];
    long _cachedBytes;
    bool _disposed;

    public AsyncSstReaderCache(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        _budgetBytes = budgetBytes;
    }

    public int CachedReaderCount
    {
        get
        {
            lock (_gate)
            {
                return _recency.Count;
            }
        }
    }

    public void Dispose()
    {
        List<AsyncSstReader> readers;
        lock (_gate)
        {
            _disposed = true;
            readers = DetachAll();
        }

        DisposeAll(readers);
    }

    public async ValueTask<AsyncSstReader> GetOrOpenAsync(
        FileMeta file,
        Func<CancellationToken, ValueTask<AsyncSstReader>> openReader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(openReader);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task pending;
            Slot? opener = null;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var slot = GetOrCreateSlot(file);
                if (slot.Reader is { } cached)
                {
                    Touch(slot);
                    return cached.Share();
                }

                if (slot.Opening is null)
                {
                    slot.Opening = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    opener = slot;
                }

                pending = slot.Opening.Task;
            }

            if (opener is not null)
            {
                // Started outside the lock so a slow open never blocks unrelated SSTs.
                _ = OpenAndPublishAsync(opener, openReader);
            }

            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Drops every cached reader published under <paramref name="name" />, whatever its manifest
    ///     identity.
    /// </summary>
    public void RemoveFile(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var readers = new List<AsyncSstReader>();
        lock (_gate)
        {
            var matches = _slots.Keys
                .Where(identity => StringComparer.Ordinal.Equals(identity.Name, name))
                .ToArray();
            foreach (var identity in matches)
            {
                if (_slots.Remove(identity, out var slot) && Detach(slot) is { } reader)
                {
                    readers.Add(reader);
                }
            }
        }

        DisposeAll(readers);
    }

    async Task OpenAndPublishAsync(
        Slot slot,
        Func<CancellationToken, ValueTask<AsyncSstReader>> openReader)
    {
        var completion = slot.Opening!;
        AsyncSstReader? opened = null;
        try
        {
            // The open is shared, so it must not be cancelled by whichever caller started it.
            opened = await openReader(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                slot.Opening = null;
                RemoveEmptySlot(slot);
            }

            completion.SetException(exception);
            return;
        }

        List<AsyncSstReader> evicted;
        var discard = false;
        lock (_gate)
        {
            slot.Opening = null;
            if (_disposed ||
                !_slots.TryGetValue(slot.Identity, out var current) ||
                !ReferenceEquals(current, slot))
            {
                discard = true;
                evicted = [];
            }
            else
            {
                slot.Reader = opened;
                slot.Bytes = opened.EstimatedMetadataBytes;
                _cachedBytes = checked(_cachedBytes + slot.Bytes);
                slot.Node = _recency.AddLast(slot);
                evicted = EvictOverBudget(slot);
            }
        }

        if (discard)
        {
            evicted.Add(opened);
        }

        DisposeAll(evicted);
        completion.SetResult();
    }

    Slot GetOrCreateSlot(FileMeta file)
    {
        var identity = SstFileIdentity.Of(file);
        if (!_slots.TryGetValue(identity, out var slot))
        {
            slot = new Slot(identity);
            _slots.Add(identity, slot);
        }

        return slot;
    }

    void Touch(Slot slot)
    {
        if (slot.Node is { List: not null } node)
        {
            _recency.Remove(node);
            _recency.AddLast(node);
        }
    }

    List<AsyncSstReader> EvictOverBudget(Slot keep)
    {
        var evicted = new List<AsyncSstReader>();
        var node = _recency.First;
        while (_cachedBytes > _budgetBytes && node is not null)
        {
            var next = node.Next;
            var slot = node.Value;
            if (!ReferenceEquals(slot, keep))
            {
                _slots.Remove(slot.Identity);
                if (Detach(slot) is { } reader)
                {
                    evicted.Add(reader);
                }
            }

            node = next;
        }

        return evicted;
    }

    AsyncSstReader? Detach(Slot slot)
    {
        var reader = slot.Reader;
        slot.Reader = null;
        if (slot.Node is { List: not null } node)
        {
            _recency.Remove(node);
            _cachedBytes -= slot.Bytes;
        }

        return reader;
    }

    List<AsyncSstReader> DetachAll()
    {
        var readers = new List<AsyncSstReader>();
        foreach (var slot in _slots.Values)
        {
            if (Detach(slot) is { } reader)
            {
                readers.Add(reader);
            }
        }

        _slots.Clear();
        return readers;
    }

    void RemoveEmptySlot(Slot slot)
    {
        if (slot.Reader is null &&
            _slots.TryGetValue(slot.Identity, out var current) &&
            ReferenceEquals(current, slot))
        {
            _slots.Remove(slot.Identity);
        }
    }

    static void DisposeAll(IEnumerable<AsyncSstReader> readers)
    {
        foreach (var reader in readers)
        {
            reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    sealed class Slot(SstFileIdentity identity)
    {
        public SstFileIdentity Identity { get; } = identity;

        public AsyncSstReader? Reader { get; set; }

        public long Bytes { get; set; }

        public LinkedListNode<Slot>? Node { get; set; }

        public TaskCompletionSource? Opening { get; set; }
    }
}
