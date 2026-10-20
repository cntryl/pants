namespace Cntryl.Pants.Storage.Internal.Cache;

sealed class SstReaderCache : IDisposable
{
    internal const int DefaultOpenReaderHandleBudget = 256;

    readonly long _budgetBytes;
    readonly object _gate = new();
    readonly LinkedList<ReaderEntry> _idleOrder = [];
    readonly Func<string, SstReader> _openReader;
    readonly SstReaderHandleBudget _openReaderHandleBudget;
    readonly HashSet<ReaderEntry> _retiredReaders = [];
    readonly Dictionary<SstFileIdentity, ReaderSlot> _slots = [];
    bool _disposeCompleted;

    int _disposed;
    int _openingReaders;
    long _cachedBytes;

    public SstReaderCache()
        : this(long.MaxValue, new SstReaderHandleBudget(DefaultOpenReaderHandleBudget), SstReader.Open)
    {
    }

    /// <summary>
    ///     Caches parsed readers up to <paramref name="budgetBytes" /> of estimated metadata,
    ///     evicting the least recently used readers nobody currently holds, with a separate
    ///     <see cref="DefaultOpenReaderHandleBudget" /> for cached and uncached open readers.
    /// </summary>
    public SstReaderCache(long budgetBytes)
        : this(budgetBytes, new SstReaderHandleBudget(DefaultOpenReaderHandleBudget), SstReader.Open)
    {
    }

    internal SstReaderCache(Func<string, SstReader> openReader)
        : this(long.MaxValue, new SstReaderHandleBudget(DefaultOpenReaderHandleBudget), openReader)
    {
    }

    internal SstReaderCache(long budgetBytes, Func<string, SstReader> openReader)
        : this(budgetBytes, new SstReaderHandleBudget(DefaultOpenReaderHandleBudget), openReader)
    {
    }

    internal SstReaderCache(
        long budgetBytes,
        SstReaderHandleBudget openReaderHandleBudget,
        Func<string, SstReader> openReader)
    {
        ArgumentNullException.ThrowIfNull(openReader);
        ArgumentNullException.ThrowIfNull(openReaderHandleBudget);
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        _budgetBytes = budgetBytes;
        _openReaderHandleBudget = openReaderHandleBudget;
        _openReader = openReader;
    }

    internal SstReaderCache(long budgetBytes, SstReaderHandleBudget openReaderHandleBudget)
        : this(budgetBytes, openReaderHandleBudget, SstReader.Open)
    {
    }

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed != 0)
            {
                while (!_disposeCompleted)
                {
                    Monitor.Wait(_gate);
                }

                return;
            }

            Volatile.Write(ref _disposed, 1);
            foreach (var slot in _slots.Values)
            {
                if (slot.Reader is not { } entry)
                {
                    continue;
                }

                slot.Reader = null;
                Retire(entry);
            }

            _slots.Clear();
            while (_openingReaders != 0 || _retiredReaders.Count != 0)
            {
                Monitor.Wait(_gate);
            }

            _disposeCompleted = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    ///     Leases the cached reader for <paramref name="file" />, opening it from
    ///     <paramref name="path" /> on a miss. Readers are keyed by manifest identity, so a name
    ///     reused for a different manifest entry opens its own reader instead of sharing the old one.
    /// </summary>
    public SstReaderLease GetOrAdd(SstFileIdentity file, string path, out bool cacheHit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ReaderSlot slot;
        int generation;
        IDisposable handleReservation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            slot = GetOrCreateSlot(file);
            while (true)
            {
                if (slot.Reader is { } cached)
                {
                    cacheHit = true;
                    return Acquire(cached);
                }

                if (!slot.Opening)
                {
                    break;
                }

                // Concurrent first opens of one SST share a single open: wait for its owner, and
                // take over if that open fails.
                Monitor.Wait(_gate);
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                slot = GetOrCreateSlot(file);
            }

            try
            {
                handleReservation = ReserveOpenReaderHandleUnderLock();
            }
            catch
            {
                RemoveUnusedSlot(file, slot);
                throw;
            }

            generation = slot.Generation;
            slot.Opening = true;
            slot.OpeningReaders++;
            _openingReaders++;
        }

        SstReader created;
        try
        {
            created = _openReader(path);
        }
        catch
        {
            try
            {
                handleReservation.Dispose();
            }
            finally
            {
                CompleteOpening(file, slot);
            }

            throw;
        }

        var handleReservationTransferred = false;
        var removeUnusedSlot = false;
        lock (_gate)
        {
            try
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);

                if (slot.Generation != generation)
                {
                    removeUnusedSlot = true;
                    throw new FileNotFoundException(
                        $"SST reader '{file.Name}' was removed while it was opening.",
                        path);
                }

                if (slot.Reader is { } winner)
                {
                    var lease = Acquire(winner);
                    DisposeUnpublishedReader(created, handleReservation);
                    handleReservationTransferred = true;
                    cacheHit = true;
                    return lease;
                }

                var added = new ReaderEntry(file, created, handleReservation) { References = 1 };
                var leaseForCreated = new SstReaderLease(created, () => Release(added));
                _cachedBytes = checked(_cachedBytes + added.Bytes);
                added.Node = _idleOrder.AddLast(added);
                slot.Reader = added;
                handleReservationTransferred = true;
                EvictIdleOverBudget();
                cacheHit = false;
                return leaseForCreated;
            }
            catch
            {
                if (!handleReservationTransferred)
                {
                    DisposeUnpublishedReader(created, handleReservation);
                    handleReservationTransferred = true;
                }

                throw;
            }
            finally
            {
                CompleteOpeningUnderLock(slot);
                if (removeUnusedSlot)
                {
                    RemoveUnusedSlot(file, slot);
                }
            }

        }
    }

    /// <summary>Opens an uncached reader under the same handle limit as cached readers.</summary>
    public SstReader OpenUncached(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        IDisposable handleReservation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            handleReservation = ReserveOpenReaderHandleUnderLock();
            _openingReaders++;
        }

        SstReader? reader = null;
        var openingCompleted = false;
        try
        {
            var opened = SstReader.Open(path, handleReservation);
            reader = opened;
            lock (_gate)
            {
                if (_disposed != 0)
                {
                    opened.Dispose();
                    reader = null;
                    ObjectDisposedException.ThrowIf(_disposed != 0, this);
                }

                _openingReaders--;
                openingCompleted = true;
                Monitor.PulseAll(_gate);
                return opened;
            }
        }
        finally
        {
            if (!openingCompleted)
            {
                lock (_gate)
                {
                    _openingReaders--;
                    Monitor.PulseAll(_gate);
                }

                if (reader is not null)
                {
                    reader.Dispose();
                }
                else
                {
                    handleReservation.Dispose();
                }
            }
        }
    }

    /// <summary>
    ///     Drops every cached reader published under <paramref name="fileName" />, whatever its
    ///     manifest identity. Leased readers stay valid until their holders release them.
    /// </summary>
    public void RemoveFile(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        lock (_gate)
        {
            var matches = _slots
                .Where(pair => StringComparer.Ordinal.Equals(pair.Key.Name, fileName))
                .ToArray();
            foreach (var (file, slot) in matches)
            {
                slot.Generation = checked(slot.Generation + 1);
                if (slot.Reader is { } entry)
                {
                    slot.Reader = null;
                    Retire(entry);
                }

                RemoveUnusedSlot(file, slot);
            }
        }
    }

    public IReadOnlyList<string> SnapshotFiles()
    {
        lock (_gate)
        {
            return _slots
                .Where(static pair => pair.Value.Reader is not null)
                .Select(static pair => pair.Key.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
    }

    SstReaderLease Acquire(ReaderEntry entry)
    {
        entry.References = checked(entry.References + 1);
        if (entry.Node is { List: not null } node)
        {
            _idleOrder.Remove(node);
            _idleOrder.AddLast(node);
        }

        return new SstReaderLease(entry.Reader, () => Release(entry));
    }

    void CompleteOpening(SstFileIdentity file, ReaderSlot slot)
    {
        lock (_gate)
        {
            CompleteOpeningUnderLock(slot);
            RemoveUnusedSlot(file, slot);
        }
    }

    void CompleteOpeningUnderLock(ReaderSlot slot)
    {
        slot.Opening = false;
        slot.OpeningReaders--;
        _openingReaders--;
        Monitor.PulseAll(_gate);
    }

    ReaderSlot GetOrCreateSlot(SstFileIdentity file)
    {
        if (!_slots.TryGetValue(file, out var slot))
        {
            slot = new ReaderSlot();
            _slots.Add(file, slot);
        }

        return slot;
    }

    void Release(ReaderEntry entry)
    {
        lock (_gate)
        {
            entry.References--;
            if (entry.References == 0 && entry.Retired)
            {
                try
                {
                    entry.Dispose();
                }
                finally
                {
                    _retiredReaders.Remove(entry);
                    Monitor.PulseAll(_gate);
                }
            }
            else if (entry.References == 0)
            {
                EvictIdleOverBudget();
            }
        }
    }

    /// <summary>
    ///     Retires least-recently-used readers that no caller holds until the cache fits its
    ///     budget. A held reader is never evicted, so the cache may exceed the budget while leased.
    /// </summary>
    void EvictIdleOverBudget()
    {
        var node = _idleOrder.First;
        while (_cachedBytes > _budgetBytes && node is not null)
        {
            var next = node.Next;
            var entry = node.Value;
            if (entry.References == 0 &&
                _slots.TryGetValue(entry.File, out var slot) &&
                ReferenceEquals(slot.Reader, entry))
            {
                slot.Reader = null;
                Retire(entry);
                RemoveUnusedSlot(entry.File, slot);
            }

            node = next;
        }
    }

    IDisposable ReserveOpenReaderHandleUnderLock()
    {
        if (_openReaderHandleBudget.TryReserve(out var reservation))
        {
            return reservation;
        }

        while (_idleOrder.First is { } node)
        {
            var entry = node.Value;
            if (_slots.TryGetValue(entry.File, out var slot) && ReferenceEquals(slot.Reader, entry))
            {
                slot.Reader = null;
                Retire(entry);
                RemoveUnusedSlot(entry.File, slot);
            }
            else
            {
                _idleOrder.Remove(node);
                entry.Node = null;
            }

            if (_openReaderHandleBudget.TryReserve(out reservation))
            {
                return reservation;
            }
        }

        if (_openReaderHandleBudget.TryReserve(out reservation))
        {
            return reservation;
        }

        throw new PantsResourceLimitException(
            $"Opening another SST reader would exceed the " +
            $"{_openReaderHandleBudget.Limit}-handle budget " +
            $"({_openReaderHandleBudget.Current} reader handles are already reserved).");
    }

    static void DisposeUnpublishedReader(SstReader reader, IDisposable handleReservation)
    {
        try
        {
            reader.Dispose();
        }
        finally
        {
            handleReservation.Dispose();
        }
    }

    void RemoveUnusedSlot(SstFileIdentity file, ReaderSlot slot)
    {
        if (slot.Reader is null && slot.OpeningReaders == 0 &&
            _slots.TryGetValue(file, out var current) && ReferenceEquals(current, slot))
        {
            _slots.Remove(file);
        }
    }

    void Retire(ReaderEntry entry)
    {
        entry.Retired = true;
        if (entry.Node is { List: not null } node)
        {
            _idleOrder.Remove(node);
            _cachedBytes -= entry.Bytes;
        }

        if (entry.References == 0)
        {
            entry.Dispose();
            return;
        }

        _retiredReaders.Add(entry);
    }

    sealed class ReaderEntry(SstFileIdentity file, SstReader reader, IDisposable handleReservation)
    {
        public SstFileIdentity File { get; } = file;

        public SstReader Reader { get; } = reader;

        public long Bytes { get; } = reader.EstimatedMetadataBytes;

        public IDisposable HandleReservation { get; } = handleReservation;

        public LinkedListNode<ReaderEntry>? Node { get; set; }

        public int References { get; set; }

        public bool Retired { get; set; }

        public void Dispose()
        {
            try
            {
                Reader.Dispose();
            }
            finally
            {
                HandleReservation.Dispose();
            }
        }
    }

    sealed class ReaderSlot
    {
        public int Generation { get; set; }

        public bool Opening { get; set; }

        public int OpeningReaders { get; set; }

        public ReaderEntry? Reader { get; set; }
    }
}
