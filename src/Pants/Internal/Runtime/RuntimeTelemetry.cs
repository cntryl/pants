using System.Diagnostics;

namespace Pants;

internal sealed class RuntimeTelemetry
{
    private long _readOnlyTransactionsBegun;
    private long _readOnlySnapshotCacheHits;
    private long _readOnlySnapshotCacheMisses;
    private long _snapshotsRegistered;
    private long _snapshotsUnregistered;
    private long _readsTotal;
    private long _sstsTouchedTotal;
    private long _l0SstsTouchedTotal;
    private long _blocksReadTotal;
    private long _sstReaderCacheHits;
    private long _sstReaderCacheMisses;
    private long _sstBlockCacheHits;
    private long _sstBlockCacheMisses;
    private long _candidateSstFilesChecked;
    private long _candidateBlocksChecked;
    private long _dataBlocksRead;
    private long _bloomChecks;
    private long _bloomRejects;
    private long _rangeTombstoneScans;
    private long _sstBudgetViolations;
    private long _blockBudgetViolations;
    private long _writeConflictsPoint;
    private long _writeConflictsRange;
    private long _compactionsRun;
    private long _compactionBytesRewritten;
    private long _walAppendCount;
    private long _walFlushCount;
    private long _walFsyncCount;
    private long _walAppendNanosecondsTotal;
    private long _walFsyncNanosecondsTotal;
    private long _walFsyncNanosecondsMaximum;
    private long _durabilityWaitersFannedOut;
    private long _flushBuildCount;
    private long _flushBuildNanosecondsTotal;
    private long _flushBuildNanosecondsMaximum;
    private long _flushPublishCount;
    private long _flushPublishNanosecondsTotal;
    private long _flushPublishNanosecondsMaximum;

    public long WriteConflictsPointTotal => Volatile.Read(ref _writeConflictsPoint);

    public long WriteConflictsRangeTotal => Volatile.Read(ref _writeConflictsRange);

    public long CompactionsRun => Volatile.Read(ref _compactionsRun);

    public long CompactionBytesRewritten => Volatile.Read(ref _compactionBytesRewritten);

    public long WalAppendCount => Volatile.Read(ref _walAppendCount);

    public long WalFlushCount => Volatile.Read(ref _walFlushCount);

    public long WalFsyncCount => Volatile.Read(ref _walFsyncCount);

    public long WalAppendNanosecondsTotal => Volatile.Read(ref _walAppendNanosecondsTotal);

    public long WalFsyncNanosecondsTotal => Volatile.Read(ref _walFsyncNanosecondsTotal);

    public long WalFsyncNanosecondsMaximum => Volatile.Read(ref _walFsyncNanosecondsMaximum);

    public long DurabilityWaitersFannedOut => Volatile.Read(ref _durabilityWaitersFannedOut);

    public long FlushBuildCount => Volatile.Read(ref _flushBuildCount);

    public long FlushBuildNanosecondsTotal => Volatile.Read(ref _flushBuildNanosecondsTotal);

    public long FlushBuildNanosecondsMaximum => Volatile.Read(ref _flushBuildNanosecondsMaximum);

    public long FlushPublishCount => Volatile.Read(ref _flushPublishCount);

    public long FlushPublishNanosecondsTotal => Volatile.Read(ref _flushPublishNanosecondsTotal);

    public long FlushPublishNanosecondsMaximum => Volatile.Read(ref _flushPublishNanosecondsMaximum);

    public long CacheHits => Volatile.Read(ref _sstBlockCacheHits);

    public long CacheMisses => Volatile.Read(ref _sstBlockCacheMisses);

    public long SstBloomChecks => Volatile.Read(ref _bloomChecks);

    public long SstBloomRejects => Volatile.Read(ref _bloomRejects);

    public long SstDataBlocksRead => Volatile.Read(ref _dataBlocksRead);

    public void RecordTransactionBegin(PantsTransactionMode mode)
    {
        Interlocked.Increment(ref _snapshotsRegistered);
        if (mode == PantsTransactionMode.ReadOnly)
        {
            Interlocked.Increment(ref _readOnlyTransactionsBegun);
            Interlocked.Increment(ref _readOnlySnapshotCacheHits);
        }
    }

    public void RecordSnapshotRegister() => Interlocked.Increment(ref _snapshotsRegistered);

    public void RecordReadOnlySnapshotCacheMiss() =>
        Interlocked.Increment(ref _readOnlySnapshotCacheMisses);

    public void RecordSnapshotUnregister() => Interlocked.Increment(ref _snapshotsUnregistered);

    public void RecordSstRead(
        int sstsTouched,
        int l0SstsTouched,
        int amplificationBlocksRead,
        int dataBlocksRead,
        int readerCacheHits,
        int readerCacheMisses,
        int blockCacheHits,
        int blockCacheMisses,
        int candidateBlocks,
        int bloomChecks,
        int bloomRejects,
        int rangeTombstoneScans)
    {
        Interlocked.Increment(ref _readsTotal);
        Interlocked.Add(ref _sstsTouchedTotal, sstsTouched);
        Interlocked.Add(ref _l0SstsTouchedTotal, l0SstsTouched);
        Interlocked.Add(ref _blocksReadTotal, amplificationBlocksRead);
        Interlocked.Add(ref _candidateSstFilesChecked, sstsTouched);
        Interlocked.Add(ref _candidateBlocksChecked, candidateBlocks);
        Interlocked.Add(ref _dataBlocksRead, dataBlocksRead);
        Interlocked.Add(ref _bloomChecks, bloomChecks);
        Interlocked.Add(ref _bloomRejects, bloomRejects);
        Interlocked.Add(ref _rangeTombstoneScans, rangeTombstoneScans);
        Interlocked.Add(ref _sstReaderCacheHits, readerCacheHits);
        Interlocked.Add(ref _sstReaderCacheMisses, readerCacheMisses);
        Interlocked.Add(ref _sstBlockCacheHits, blockCacheHits);
        Interlocked.Add(ref _sstBlockCacheMisses, blockCacheMisses);
        if (sstsTouched > 5)
        {
            Interlocked.Increment(ref _sstBudgetViolations);
        }

        if (amplificationBlocksRead > 20)
        {
            Interlocked.Increment(ref _blockBudgetViolations);
        }
    }

    public void RecordWriteConflict(bool rangeConflict)
    {
        if (rangeConflict)
        {
            Interlocked.Increment(ref _writeConflictsRange);
        }
        else
        {
            Interlocked.Increment(ref _writeConflictsPoint);
        }
    }

    public void RecordSstScan(
        int candidateSsts,
        int candidateBlocks,
        int dataBlocksRead,
        int readerCacheHits,
        int readerCacheMisses,
        int rangeTombstoneScans)
    {
        Interlocked.Add(ref _candidateSstFilesChecked, candidateSsts);
        Interlocked.Add(ref _candidateBlocksChecked, candidateBlocks);
        Interlocked.Add(ref _dataBlocksRead, dataBlocksRead);
        Interlocked.Add(ref _sstReaderCacheHits, readerCacheHits);
        Interlocked.Add(ref _sstReaderCacheMisses, readerCacheMisses);
        Interlocked.Add(ref _rangeTombstoneScans, rangeTombstoneScans);
    }

    public void RecordCompaction(long bytesRewritten)
    {
        Interlocked.Increment(ref _compactionsRun);
        Interlocked.Add(ref _compactionBytesRewritten, bytesRewritten);
    }

    public void RecordWalAppend(TimeSpan elapsed, PantsDurability durability)
    {
        Interlocked.Increment(ref _walAppendCount);
        Interlocked.Increment(ref _walFlushCount);
        Interlocked.Add(ref _walAppendNanosecondsTotal, ToNanoseconds(elapsed));
        if (durability == PantsDurability.Sync)
        {
            Interlocked.Increment(ref _walFsyncCount);
            long nanoseconds = ToNanoseconds(elapsed);
            Interlocked.Add(ref _walFsyncNanosecondsTotal, nanoseconds);
            SetMaximum(ref _walFsyncNanosecondsMaximum, nanoseconds);
        }
    }

    public void RecordCoalescedWalFsync(TimeSpan elapsed, int waiterCount)
    {
        long nanoseconds = ToNanoseconds(elapsed);
        Interlocked.Increment(ref _walFsyncCount);
        Interlocked.Add(ref _walFsyncNanosecondsTotal, nanoseconds);
        SetMaximum(ref _walFsyncNanosecondsMaximum, nanoseconds);
        Interlocked.Add(ref _durabilityWaitersFannedOut, waiterCount);
    }

    public void RecordFlush(TimeSpan elapsed)
    {
        long nanoseconds = ToNanoseconds(elapsed);
        Interlocked.Increment(ref _flushBuildCount);
        Interlocked.Add(ref _flushBuildNanosecondsTotal, nanoseconds);
        SetMaximum(ref _flushBuildNanosecondsMaximum, nanoseconds);
        Interlocked.Increment(ref _flushPublishCount);
        Interlocked.Add(ref _flushPublishNanosecondsTotal, nanoseconds);
        SetMaximum(ref _flushPublishNanosecondsMaximum, nanoseconds);
    }

    public PantsReadPathDiagnostics GetReadPathDiagnostics() => new()
    {
        ReadOnlyTransactionsBegun = Volatile.Read(ref _readOnlyTransactionsBegun),
        ReadOnlySnapshotCacheHits = Volatile.Read(ref _readOnlySnapshotCacheHits),
        ReadOnlySnapshotCacheMisses = Volatile.Read(ref _readOnlySnapshotCacheMisses),
        SnapshotsRegistered = Volatile.Read(ref _snapshotsRegistered),
        SnapshotsUnregistered = Volatile.Read(ref _snapshotsUnregistered),
        SstReaderCacheHits = Volatile.Read(ref _sstReaderCacheHits),
        SstReaderCacheMisses = Volatile.Read(ref _sstReaderCacheMisses),
        SstBlockCacheHits = Volatile.Read(ref _sstBlockCacheHits),
        SstBlockCacheMisses = Volatile.Read(ref _sstBlockCacheMisses),
        CandidateSstFilesChecked = Volatile.Read(ref _candidateSstFilesChecked),
        CandidateBlocksChecked = Volatile.Read(ref _candidateBlocksChecked),
        DataBlocksRead = Volatile.Read(ref _dataBlocksRead),
        BloomChecks = Volatile.Read(ref _bloomChecks),
        BloomRejects = Volatile.Read(ref _bloomRejects),
        RangeTombstoneScans = Volatile.Read(ref _rangeTombstoneScans)
    };

    public PantsReadAmplificationMetrics GetReadAmplificationMetrics()
    {
        long reads = Volatile.Read(ref _readsTotal);
        long ssts = Volatile.Read(ref _sstsTouchedTotal);
        long l0Ssts = Volatile.Read(ref _l0SstsTouchedTotal);
        long blocks = Volatile.Read(ref _blocksReadTotal);
        return new PantsReadAmplificationMetrics(
            reads,
            ssts,
            l0Ssts,
            blocks,
            Divide(ssts, reads),
            Divide(l0Ssts, reads),
            Divide(blocks, reads),
            Divide(l0Ssts, ssts),
            Divide(Volatile.Read(ref _sstBudgetViolations), reads),
            Divide(Volatile.Read(ref _blockBudgetViolations), reads));
    }

    private static double Divide(long numerator, long denominator) =>
        denominator == 0 ? 0 : (double)numerator / denominator;

    private static long ToNanoseconds(TimeSpan elapsed) =>
        checked((long)(elapsed.TotalMilliseconds * 1_000_000));

    private static void SetMaximum(ref long target, long value)
    {
        long current = Volatile.Read(ref target);
        while (value > current)
        {
            long observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
