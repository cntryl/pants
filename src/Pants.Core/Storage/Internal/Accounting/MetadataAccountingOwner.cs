namespace Cntryl.Pants.Storage.Internal.Accounting;

sealed class MetadataAccountingOwner
{
    const int MaximumPendingPublications = 1024;
    static readonly Lock IdentityGate = new();
    static ulong _nextOwner = 1;
    readonly MetadataAccountingState _state = CreateState();
    readonly TimeProvider _timeProvider;
    readonly Lock _publicationGate = new();
    readonly Dictionary<string, MetadataPublicationReceipt> _pendingPublications = new(StringComparer.Ordinal);
    bool _publicationsSealed;

    public MetadataAccountingOwner(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    public MetadataAccountingHandle Handle => new(_state);

    public ulong Identity => _state.OwnerId;

    public MetadataAccountingOperation Begin(MetadataOperationKind kind, MetadataOrigin origin, MetadataMedium medium)
    {
        lock (_publicationGate)
        {
            if (_publicationsSealed)
            {
                Invalidate();
            }

            return new MetadataAccountingOperation(_state, _timeProvider, kind, origin, medium);
        }
    }

    public void Invalidate() => _state.Invalidate();

    public long Timestamp() => _timeProvider.GetTimestamp();

    public TimeSpan Elapsed(long started) => _timeProvider.GetElapsedTime(started);

    public void RetainPublication(MetadataPublicationReceipt receipt)
    {
        lock (_publicationGate)
        {
            if (_publicationsSealed)
            {
                Invalidate();
                return;
            }

            if (_pendingPublications.TryGetValue(receipt.Name, out var existing))
            {
                if (existing.FamilyId != receipt.FamilyId || existing.SstSequence != receipt.SstSequence ||
                    existing.Bytes != receipt.Bytes || existing.ContentCrc32C != receipt.ContentCrc32C ||
                    existing.Origin != receipt.Origin || existing.Medium != receipt.Medium)
                {
                    Invalidate();
                }

                return;
            }

            if (_pendingPublications.Count == MaximumPendingPublications)
            {
                Invalidate();
                return;
            }

            _pendingPublications.Add(receipt.Name, receipt);
        }
    }

    public void CompletePublications(IReadOnlyList<FileMeta> visibleFiles)
    {
        MetadataPublicationReceipt[] pending;
        lock (_publicationGate)
        {
            pending = [.. _pendingPublications.Values];
        }

        foreach (var receipt in pending)
        {
            var matching = visibleFiles.Any(file => file.Name == receipt.Name &&
                file.ColumnFamilyId == receipt.FamilyId && file.SstSequence == receipt.SstSequence &&
                file.SizeBytes == receipt.Bytes && file.ContentCrc32C == receipt.ContentCrc32C);
            lock (_publicationGate)
            {
                if (!_pendingPublications.TryGetValue(receipt.Name, out var current) || !ReferenceEquals(current, receipt))
                {
                    continue;
                }

                _pendingPublications.Remove(receipt.Name);
            }

            if (matching)
            {
                FlushCommitted(receipt.Origin, receipt.Medium, receipt.Bytes, Elapsed(receipt.StartedTimestamp));
            }
            else
            {
                Invalidate();
            }
        }
    }

    public void InstallPublishedCut(IEnumerable<MetadataPublishedSstIdentity> publishedSsts)
    {
        foreach (var file in publishedSsts)
        {
            MetadataPublicationReceipt receipt;
            lock (_publicationGate)
            {
                if (!_pendingPublications.TryGetValue(file.Name, out receipt!))
                {
                    continue;
                }

                if (receipt.FamilyId != file.FamilyId || receipt.SstSequence != file.SstSequence ||
                    receipt.Bytes != file.Bytes || receipt.ContentCrc32C != file.ContentCrc32C)
                {
                    Invalidate();
                    continue;
                }

                _pendingPublications.Remove(file.Name);
                FlushCommitted(receipt.Origin, receipt.Medium, receipt.Bytes, Elapsed(receipt.StartedTimestamp));
            }
        }
    }

    public void SealPublications()
    {
        lock (_publicationGate)
        {
            _publicationsSealed = true;
            if (_pendingPublications.Count != 0)
            {
                Invalidate();
                _pendingPublications.Clear();
            }
        }
    }

    public void PublicationAttempt(MetadataOrigin origin, MetadataMedium medium, TimeSpan elapsed, bool failed) =>
        Record(origin, medium, MetadataCounter.PublicationAttempts, MetadataCounter.PublicationAttemptNanoseconds,
            elapsed, failed ? MetadataCounter.PublicationFailures : null);

    public void FlushCommitted(MetadataOrigin origin, MetadataMedium medium, ulong bytes, TimeSpan elapsed) =>
        Record(origin, medium, MetadataCounter.FlushCommittedCount, MetadataCounter.FullPublicationNanoseconds,
            elapsed, MetadataCounter.FlushCommittedSstBytes, bytes);

    public void CompactionCommitted(MetadataMedium medium, ulong bytes)
    {
        var counters = new ulong[MetadataAccountingArithmetic.CounterCount];
        counters[(int)MetadataCounter.CompactionCommittedCount] = 1;
        counters[(int)MetadataCounter.CompactionCommittedSstBytes] = bytes;
        FoldPublication((int)MetadataOrigin.CompactionBeforeGc * 2 + (int)medium, counters, bytes == 0);
    }

    void Record(MetadataOrigin origin, MetadataMedium medium, MetadataCounter count, MetadataCounter duration,
        TimeSpan elapsed, MetadataCounter? other, ulong otherValue = 1)
    {
        var counters = new ulong[MetadataAccountingArithmetic.CounterCount];
        counters[(int)count] = 1;
        counters[(int)duration] = MetadataAccountingArithmetic.Nanoseconds(elapsed, out var valid);
        if (other is { } counter)
        {
            counters[(int)counter] = otherValue;
        }

        FoldPublication((int)origin * 2 + (int)medium, counters, !valid);
    }

    void FoldPublication(int bucket, ulong[] counters, bool overflow)
    {
        lock (_publicationGate)
        {
            if (_publicationsSealed)
            {
                Invalidate();
                return;
            }

            _state.Fold(bucket, counters, overflow);
        }
    }

    static MetadataAccountingState CreateState()
    {
        lock (IdentityGate)
        {
            var id = _nextOwner;
            _nextOwner = id is 0 or ulong.MaxValue ? 0 : id + 1;
            return new MetadataAccountingState(id);
        }
    }
}
