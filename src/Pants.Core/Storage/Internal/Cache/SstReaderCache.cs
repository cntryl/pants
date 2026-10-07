namespace Cntryl.Pants.Storage.Internal.Cache;

sealed class SstReaderCache : IDisposable
{
    readonly long _budgetBytes;
    readonly object _gate = new();
    readonly LinkedList<ReaderEntry> _idleOrder = [];
    readonly Func<string, SstReader> _openReader;
    readonly HashSet<ReaderEntry> _retiredReaders = [];
    readonly Dictionary<string, ReaderSlot> _slots = new(StringComparer.Ordinal);
    bool _disposeCompleted;

    int _disposed;
    int _openingReaders;
    long _cachedBytes;

    public SstReaderCache()
        : this(long.MaxValue, SstReader.Open)
    {
    }

    /// <summary>
    ///     Caches parsed readers up to <paramref name="budgetBytes" /> of estimated metadata,
    ///     evicting the least recently used readers nobody currently holds.
    /// </summary>
    public SstReaderCache(long budgetBytes)
        : this(budgetBytes, SstReader.Open)
    {
    }

    internal SstReaderCache(Func<string, SstReader> openReader)
        : this(long.MaxValue, openReader)
    {
    }

    internal SstReaderCache(long budgetBytes, Func<string, SstReader> openReader)
    {
        ArgumentNullException.ThrowIfNull(openReader);
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        _budgetBytes = budgetBytes;
        _openReader = openReader;
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

    public SstReaderLease GetOrAdd(string fileName, string path, out bool cacheHit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ReaderSlot slot;
        int generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            slot = GetOrCreateSlot(fileName);
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
                slot = GetOrCreateSlot(fileName);
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
            CompleteOpening(fileName, slot);
            throw;
        }

        lock (_gate)
        {
            CompleteOpeningUnderLock(slot);
            if (_disposed != 0)
            {
                created.Dispose();
                throw new ObjectDisposedException(nameof(SstReaderCache));
            }

            if (slot.Generation != generation)
            {
                created.Dispose();
                RemoveUnusedSlot(fileName, slot);
                throw new FileNotFoundException(
                    $"SST reader '{fileName}' was removed while it was opening.",
                    path);
            }

            if (slot.Reader is { } winner)
            {
                created.Dispose();
                cacheHit = true;
                return Acquire(winner);
            }

            var added = new ReaderEntry(fileName, created) { References = 1 };
            slot.Reader = added;
            _cachedBytes = checked(_cachedBytes + added.Bytes);
            added.Node = _idleOrder.AddLast(added);
            EvictIdleOverBudget();
            cacheHit = false;
            return new SstReaderLease(created, () => Release(added));
        }
    }

    public void RemoveFile(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        lock (_gate)
        {
            if (!_slots.TryGetValue(fileName, out var slot))
            {
                return;
            }

            slot.Generation = checked(slot.Generation + 1);
            if (slot.Reader is { } entry)
            {
                slot.Reader = null;
                Retire(entry);
            }

            RemoveUnusedSlot(fileName, slot);
        }
    }

    public IReadOnlyList<string> SnapshotFiles()
    {
        lock (_gate)
        {
            return _slots
                .Where(static pair => pair.Value.Reader is not null)
                .Select(static pair => pair.Key)
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

    void CompleteOpening(string fileName, ReaderSlot slot)
    {
        lock (_gate)
        {
            CompleteOpeningUnderLock(slot);
            RemoveUnusedSlot(fileName, slot);
        }
    }

    void CompleteOpeningUnderLock(ReaderSlot slot)
    {
        slot.Opening = false;
        slot.OpeningReaders--;
        _openingReaders--;
        Monitor.PulseAll(_gate);
    }

    ReaderSlot GetOrCreateSlot(string fileName)
    {
        if (!_slots.TryGetValue(fileName, out var slot))
        {
            slot = new ReaderSlot();
            _slots.Add(fileName, slot);
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
                entry.Reader.Dispose();
                _retiredReaders.Remove(entry);
                Monitor.PulseAll(_gate);
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
                _slots.TryGetValue(entry.FileName, out var slot) &&
                ReferenceEquals(slot.Reader, entry))
            {
                slot.Reader = null;
                Retire(entry);
                RemoveUnusedSlot(entry.FileName, slot);
            }

            node = next;
        }
    }

    void RemoveUnusedSlot(string fileName, ReaderSlot slot)
    {
        if (slot.Reader is null && slot.OpeningReaders == 0 &&
            _slots.TryGetValue(fileName, out var current) && ReferenceEquals(current, slot))
        {
            _slots.Remove(fileName);
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
            entry.Reader.Dispose();
            return;
        }

        _retiredReaders.Add(entry);
    }

    sealed class ReaderEntry(string fileName, SstReader reader)
    {
        public string FileName { get; } = fileName;

        public SstReader Reader { get; } = reader;

        public long Bytes { get; } = reader.EstimatedMetadataBytes;

        public LinkedListNode<ReaderEntry>? Node { get; set; }

        public int References { get; set; }

        public bool Retired { get; set; }
    }

    sealed class ReaderSlot
    {
        public int Generation { get; set; }

        public bool Opening { get; set; }

        public int OpeningReaders { get; set; }

        public ReaderEntry? Reader { get; set; }
    }
}
