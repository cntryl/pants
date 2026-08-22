using System.Collections.Concurrent;
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
    private long _keyRangeRejects;
    private long _bloomChecks;
    private long _bloomRejects;
    private long _bloomTruePositives;
    private long _bloomFalsePositives;
    private long _rangeTombstoneScans;
    private long _sstBudgetViolations;
    private long _blockBudgetViolations;
    private long _readAmplificationCompactionTriggers;
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
    long _flushEnqueued;
    long _flushFailures;
    long _flushRetries;
    int _pendingCloudUploads;
    readonly ConcurrentDictionary<ulong, long> _cloudWalAcknowledgementStarts = new();
    long _walLastSyncedSequence;
    long _writeStallsMemory;
    long _writeStallsNoSpace;
    long _cloudAsyncWalSegmentsSealed;
    long _cloudAsyncWalBytesSealed;
    long _cloudAsyncWalSealLatencyMicroseconds;
    long _cloudAsyncWalUploadsStarted;
    long _cloudAsyncWalUploadsCompleted;
    long _cloudAsyncWalUploadsFailed;
    long _cloudAsyncWalUploadLatencyMicroseconds;
    long _cloudAsyncWalAcknowledgementLatencyMicroseconds;

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

    public long WalLastSyncedSequence => Volatile.Read(ref _walLastSyncedSequence);

    public long DurabilityWaitersFannedOut => Volatile.Read(ref _durabilityWaitersFannedOut);

    public long FlushBuildCount => Volatile.Read(ref _flushBuildCount);

    public long FlushBuildNanosecondsTotal => Volatile.Read(ref _flushBuildNanosecondsTotal);

    public long FlushBuildNanosecondsMaximum => Volatile.Read(ref _flushBuildNanosecondsMaximum);

    public long FlushPublishCount => Volatile.Read(ref _flushPublishCount);

    public long FlushPublishNanosecondsTotal => Volatile.Read(ref _flushPublishNanosecondsTotal);

    public long FlushPublishNanosecondsMaximum => Volatile.Read(ref _flushPublishNanosecondsMaximum);

    public long FlushEnqueuedTotal => Volatile.Read(ref _flushEnqueued);

    public long FlushFailuresTotal => Volatile.Read(ref _flushFailures);

    public long FlushRetriesTotal => Volatile.Read(ref _flushRetries);

    public int PendingCloudUploads => Volatile.Read(ref _pendingCloudUploads);

    public long WriteStallsMemoryTotal => Volatile.Read(ref _writeStallsMemory);

    public long WriteStallsNoSpaceTotal => Volatile.Read(ref _writeStallsNoSpace);

    public long CloudAsyncWalSegmentsSealed => Volatile.Read(ref _cloudAsyncWalSegmentsSealed);

    public long CloudAsyncWalBytesSealed => Volatile.Read(ref _cloudAsyncWalBytesSealed);

    public long CloudAsyncWalSealLatencyMicroseconds =>
        Volatile.Read(ref _cloudAsyncWalSealLatencyMicroseconds);

    public long CloudAsyncWalUploadsStarted => Volatile.Read(ref _cloudAsyncWalUploadsStarted);

    public long CloudAsyncWalUploadsCompleted => Volatile.Read(ref _cloudAsyncWalUploadsCompleted);

    public long CloudAsyncWalUploadsFailed => Volatile.Read(ref _cloudAsyncWalUploadsFailed);

    public long CloudAsyncWalUploadLatencyMicroseconds =>
        Volatile.Read(ref _cloudAsyncWalUploadLatencyMicroseconds);

    public long CloudAsyncWalAcknowledgementLatencyMicroseconds =>
        Volatile.Read(ref _cloudAsyncWalAcknowledgementLatencyMicroseconds);

    public long CacheHits => Volatile.Read(ref _sstBlockCacheHits);

    public long CacheMisses => Volatile.Read(ref _sstBlockCacheMisses);

    public long SstBloomChecks => Volatile.Read(ref _bloomChecks);

    public long SstBloomRejects => Volatile.Read(ref _bloomRejects);

    public long SstBloomTruePositives => Volatile.Read(ref _bloomTruePositives);

    public long SstBloomFalsePositives => Volatile.Read(ref _bloomFalsePositives);

    public long SstKeyRangeRejects => Volatile.Read(ref _keyRangeRejects);

    public long ReadAmplificationCompactionTriggers =>
        Volatile.Read(ref _readAmplificationCompactionTriggers);

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

    public bool RecordSstRead(SstReadSample sample)
    {
        Interlocked.Increment(ref _readsTotal);
        Interlocked.Add(ref _sstsTouchedTotal, sample.SstsTouched);
        Interlocked.Add(ref _l0SstsTouchedTotal, sample.L0SstsTouched);
        Interlocked.Add(ref _blocksReadTotal, sample.AmplificationBlocksRead);
        Interlocked.Add(ref _candidateSstFilesChecked, sample.SstsTouched);
        Interlocked.Add(ref _candidateBlocksChecked, sample.CandidateBlocks);
        Interlocked.Add(ref _dataBlocksRead, sample.DataBlocksRead);
        Interlocked.Add(ref _keyRangeRejects, sample.KeyRangeRejects);
        Interlocked.Add(ref _bloomChecks, sample.BloomChecks);
        Interlocked.Add(ref _bloomRejects, sample.BloomTrueNegatives);
        Interlocked.Add(ref _bloomTruePositives, sample.BloomTruePositives);
        Interlocked.Add(ref _bloomFalsePositives, sample.BloomFalsePositives);
        Interlocked.Add(ref _rangeTombstoneScans, sample.RangeTombstoneScans);
        Interlocked.Add(ref _sstReaderCacheHits, sample.ReaderCacheHits);
        Interlocked.Add(ref _sstReaderCacheMisses, sample.ReaderCacheMisses);
        Interlocked.Add(ref _sstBlockCacheHits, sample.BlockCacheHits);
        Interlocked.Add(ref _sstBlockCacheMisses, sample.BlockCacheMisses);
        if (sample.SstsTouched > ReadAmplificationBudget.MaximumSstsPerRead)
        {
            Interlocked.Increment(ref _sstBudgetViolations);
        }

        if (sample.AmplificationBlocksRead > ReadAmplificationBudget.MaximumBlocksPerRead)
        {
            Interlocked.Increment(ref _blockBudgetViolations);
        }

        return sample.ExceedsBudget;
    }

    public void RecordReadAmplificationCompactionTrigger() =>
        Interlocked.Increment(ref _readAmplificationCompactionTriggers);

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

    public void RecordWalAppend(
        TimeSpan elapsed,
        PantsDurability durability,
        long sequence)
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
            SetMaximum(ref _walLastSyncedSequence, sequence);
        }
    }

    public void RecordCoalescedWalFsync(
        TimeSpan elapsed,
        int waiterCount,
        long sequence)
    {
        long nanoseconds = ToNanoseconds(elapsed);
        Interlocked.Increment(ref _walFsyncCount);
        Interlocked.Add(ref _walFsyncNanosecondsTotal, nanoseconds);
        SetMaximum(ref _walFsyncNanosecondsMaximum, nanoseconds);
        Interlocked.Add(ref _durabilityWaitersFannedOut, waiterCount);
        SetMaximum(ref _walLastSyncedSequence, sequence);
    }

    public void RecordWalFsyncBoundary(TimeSpan elapsed, long sequence)
    {
        var nanoseconds = ToNanoseconds(elapsed);
        Interlocked.Increment(ref _walFsyncCount);
        Interlocked.Add(ref _walFsyncNanosecondsTotal, nanoseconds);
        SetMaximum(ref _walFsyncNanosecondsMaximum, nanoseconds);
        SetMaximum(ref _walLastSyncedSequence, sequence);
    }

    public void RecordWalDurabilityBoundary(long sequence) =>
        SetMaximum(ref _walLastSyncedSequence, sequence);

    public void RecordWriteStallNoSpace()
    {
        Interlocked.Increment(ref _writeStallsNoSpace);
    }

    public void RecordWriteStallMemory() => Interlocked.Increment(ref _writeStallsMemory);

    public void RecordCloudAsyncWalSegmentSealed(
        ulong segmentId,
        long bytes,
        TimeSpan elapsed)
    {
        Interlocked.Increment(ref _cloudAsyncWalSegmentsSealed);
        Interlocked.Add(ref _cloudAsyncWalBytesSealed, bytes);
        Interlocked.Add(
            ref _cloudAsyncWalSealLatencyMicroseconds,
            ToMicroseconds(elapsed));
        _cloudWalAcknowledgementStarts.TryAdd(segmentId, Stopwatch.GetTimestamp());
    }

    public void RecordCloudAsyncWalUploadStarted() =>
        Interlocked.Increment(ref _cloudAsyncWalUploadsStarted);

    public void RecordCloudAsyncWalUploadCompleted(TimeSpan elapsed)
    {
        Interlocked.Increment(ref _cloudAsyncWalUploadsCompleted);
        Interlocked.Add(
            ref _cloudAsyncWalUploadLatencyMicroseconds,
            ToMicroseconds(elapsed));
    }

    public void RecordCloudAsyncWalUploadFailed() =>
        Interlocked.Increment(ref _cloudAsyncWalUploadsFailed);

    public void RecordCloudAsyncWalAcknowledged(ulong segmentId)
    {
        if (_cloudWalAcknowledgementStarts.TryRemove(segmentId, out var started))
        {
            Interlocked.Add(
                ref _cloudAsyncWalAcknowledgementLatencyMicroseconds,
                ToMicroseconds(Stopwatch.GetElapsedTime(started)));
        }
    }

    public void RecordFlush(TimeSpan elapsed)
    {
        RecordFlushBuild(elapsed);
        RecordFlushPublication(elapsed);
    }

    public void RecordFlushBuild(TimeSpan elapsed)
    {
        var nanoseconds = ToNanoseconds(elapsed);
        Interlocked.Increment(ref _flushBuildCount);
        Interlocked.Add(ref _flushBuildNanosecondsTotal, nanoseconds);
        SetMaximum(ref _flushBuildNanosecondsMaximum, nanoseconds);
    }

    public void RecordFlushPublication(TimeSpan elapsed)
    {
        var nanoseconds = ToNanoseconds(elapsed);
        Interlocked.Increment(ref _flushPublishCount);
        Interlocked.Add(ref _flushPublishNanosecondsTotal, nanoseconds);
        SetMaximum(ref _flushPublishNanosecondsMaximum, nanoseconds);
    }

    public void RecordFlushEnqueued() => Interlocked.Increment(ref _flushEnqueued);

    public void RecordFlushRetry() => Interlocked.Increment(ref _flushRetries);

    public void RecordFlushFailure() => Interlocked.Increment(ref _flushFailures);

    public void RecordCloudUploadPending() => Interlocked.Increment(ref _pendingCloudUploads);

    public void RecordCloudUploadCompleted() => Interlocked.Decrement(ref _pendingCloudUploads);

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
        KeyRangeRejects = Volatile.Read(ref _keyRangeRejects),
        BloomChecks = Volatile.Read(ref _bloomChecks),
        BloomRejects = Volatile.Read(ref _bloomRejects),
        BloomTruePositives = Volatile.Read(ref _bloomTruePositives),
        BloomFalsePositives = Volatile.Read(ref _bloomFalsePositives),
        BloomTrueNegatives = Volatile.Read(ref _bloomRejects),
        RangeTombstoneScans = Volatile.Read(ref _rangeTombstoneScans)
    };

    public PantsReadAmplificationMetrics GetReadAmplificationMetrics()
    {
        long reads = Volatile.Read(ref _readsTotal);
        long ssts = Volatile.Read(ref _sstsTouchedTotal);
        long l0Ssts = Volatile.Read(ref _l0SstsTouchedTotal);
        long blocks = Volatile.Read(ref _blocksReadTotal);
        long sstBudgetViolations = Volatile.Read(ref _sstBudgetViolations);
        long blockBudgetViolations = Volatile.Read(ref _blockBudgetViolations);
        return new PantsReadAmplificationMetrics
        {
            ReadsTotal = reads,
            SstsTouchedTotal = ssts,
            L0SstsTouchedTotal = l0Ssts,
            BlocksReadTotal = blocks,
            AverageSstsPerRead = Divide(ssts, reads),
            AverageL0SstsPerRead = Divide(l0Ssts, reads),
            AverageBlocksPerRead = Divide(blocks, reads),
            L0OverlapRate = Divide(l0Ssts, ssts),
            SstBudgetViolationsTotal = sstBudgetViolations,
            BlockBudgetViolationsTotal = blockBudgetViolations,
            SstBudgetViolationRate = Divide(sstBudgetViolations, reads),
            BlockBudgetViolationRate = Divide(blockBudgetViolations, reads),
            ReaderCacheHitsTotal = Volatile.Read(ref _sstReaderCacheHits),
            ReaderCacheMissesTotal = Volatile.Read(ref _sstReaderCacheMisses),
            BlockCacheHitsTotal = Volatile.Read(ref _sstBlockCacheHits),
            BlockCacheMissesTotal = Volatile.Read(ref _sstBlockCacheMisses),
            KeyRangeRejectsTotal = Volatile.Read(ref _keyRangeRejects),
            BloomChecksTotal = Volatile.Read(ref _bloomChecks),
            BloomTruePositivesTotal = Volatile.Read(ref _bloomTruePositives),
            BloomFalsePositivesTotal = Volatile.Read(ref _bloomFalsePositives),
            BloomTrueNegativesTotal = Volatile.Read(ref _bloomRejects),
            DataBlocksReadTotal = Volatile.Read(ref _dataBlocksRead),
            CompactionTriggersTotal = Volatile.Read(ref _readAmplificationCompactionTriggers)
        };
    }

    private static double Divide(long numerator, long denominator) =>
        denominator == 0 ? 0 : (double)numerator / denominator;

    private static long ToNanoseconds(TimeSpan elapsed) =>
        checked((long)(elapsed.TotalMilliseconds * 1_000_000));

    static long ToMicroseconds(TimeSpan elapsed) =>
        checked((long)(elapsed.TotalMilliseconds * 1_000));

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
