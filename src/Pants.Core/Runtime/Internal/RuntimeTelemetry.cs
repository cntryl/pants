using System.Collections.Concurrent;
using System.Diagnostics;

namespace Cntryl.Pants.Runtime.Internal;

sealed class RuntimeTelemetry
{
    readonly ConcurrentDictionary<ulong, long> _cloudWalAcknowledgementStarts = new();
    readonly TimeProvider _timeProvider;
    readonly object _writeStallTimingGate = new();
    long _blockBudgetViolations;
    long _blocksReadTotal;
    long _bloomChecks;
    long _bloomFalsePositives;
    long _bloomRejects;
    long _bloomTruePositives;
    long _candidateBlocksChecked;
    long _candidateSstFilesChecked;
    long _cloudAsyncWalAcknowledgementLatencyMicroseconds;
    long _cloudAsyncWalBytesSealed;
    long _cloudAsyncWalSealLatencyMicroseconds;
    long _cloudAsyncWalSegmentsSealed;
    long _cloudAsyncWalUploadLatencyMicroseconds;
    long _cloudAsyncWalUploadsCompleted;
    long _cloudAsyncWalUploadsFailed;
    long _cloudAsyncWalUploadsStarted;
    long _commandLatencyNanosecondsTotal;
    long _commandsRejected;
    long _compactionBytesRewritten;
    long _compactionFailures;
    long _compactionsRun;
    long _dataBlocksRead;
    long _durabilityWaitersFannedOut;
    long _flushBuildCount;
    long _flushBuildNanosecondsMaximum;
    long _flushBuildNanosecondsTotal;
    long _flushEnqueued;
    long _flushFailures;
    long _flushPublishCount;
    long _flushPublishNanosecondsMaximum;
    long _flushPublishNanosecondsTotal;
    long _flushRetries;
    long _intentLogEntriesReplayed;
    long _intentLogReplayRuns;
    long _keyRangeRejects;
    long _l0SstsTouchedTotal;
    long _noSpaceEvents;
    int _pendingCloudUploads;
    long _rangeTombstoneScans;
    long _readAmplificationCompactionTriggers;
    long _readOnlySnapshotCacheHits;
    long _readOnlySnapshotCacheMisses;
    long _readOnlyTransactionsBegun;
    long _readsTotal;
    long _runtimeAbandonedRequests;
    long _runtimeLateResponses;
    long _salvageModeOpens;
    long _snapshotsRegistered;
    long _snapshotsUnregistered;
    long _sstBlockCacheHits;
    long _sstBlockCacheMisses;
    long _sstBudgetViolations;
    long _sstReaderCacheHits;
    long _sstReaderCacheMisses;
    long _sstsTouchedTotal;
    long _transactionsCommitted;
    long _transactionsRolledBack;
    long _walAppendCount;
    long _walAppendNanosecondsTotal;
    long _walFlushCount;
    long _walFsyncCount;
    long _walFsyncNanosecondsMaximum;
    long _walFsyncNanosecondsTotal;
    long _walLastSyncedSequence;
    long _walRecoveryBytesReplayed;
    long _walRecoveryRecordsReplayed;
    long _writeConflictsPoint;
    long _writeConflictsRange;
    long _writeStallActiveNanoseconds;
    long _writeStallNanosecondsMaximum;
    long _writeStallNanosecondsTotal;
    long? _writeStallStartedAt;
    long _writeStallsCloud;
    long _writeStallsCompaction;
    long _writeStallsMemory;
    long _writeStallsNoSpace;

    public RuntimeTelemetry(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public long WriteConflictsPointTotal => Volatile.Read(ref _writeConflictsPoint);

    public long WriteConflictsRangeTotal => Volatile.Read(ref _writeConflictsRange);

    public long CompactionsRun => Volatile.Read(ref _compactionsRun);

    public long CompactionBytesRewritten => Volatile.Read(ref _compactionBytesRewritten);

    public long CompactionFailures => Volatile.Read(ref _compactionFailures);

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

    public long WriteStallsCompactionTotal => Volatile.Read(ref _writeStallsCompaction);

    public long WriteStallsCloudTotal => Volatile.Read(ref _writeStallsCloud);

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

    public long SalvageModeOpens => Volatile.Read(ref _salvageModeOpens);

    public long NoSpaceEvents => Volatile.Read(ref _noSpaceEvents);

    public long WalRecoveryRecordsReplayed => Volatile.Read(ref _walRecoveryRecordsReplayed);

    public long WalRecoveryBytesReplayed => Volatile.Read(ref _walRecoveryBytesReplayed);

    public long IntentLogReplayRuns => Volatile.Read(ref _intentLogReplayRuns);

    public long IntentLogEntriesReplayed => Volatile.Read(ref _intentLogEntriesReplayed);

    public long RuntimeAbandonedRequests => Volatile.Read(ref _runtimeAbandonedRequests);

    public long RuntimeLateResponses => Volatile.Read(ref _runtimeLateResponses);

    public void RecordTransactionBegin(PantsTransactionMode mode)
    {
        Interlocked.Increment(ref _snapshotsRegistered);
        PantsDiagnostics.TransactionsStarted.Add(1);
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

    public void RecordTransactionCommit()
    {
        Interlocked.Increment(ref _transactionsCommitted);
        PantsDiagnostics.TransactionsCommitted.Add(1);
    }

    public void RecordTransactionRollback()
    {
        Interlocked.Increment(ref _transactionsRolledBack);
        PantsDiagnostics.TransactionsRolledBack.Add(1);
    }

    public void RecordCommandRejected()
    {
        Interlocked.Increment(ref _commandsRejected);
        PantsDiagnostics.CommandsRejected.Add(1);
    }

    public void RecordCommandLatency(TimeSpan elapsed)
    {
        Interlocked.Add(ref _commandLatencyNanosecondsTotal, ToNanoseconds(elapsed));
        PantsDiagnostics.CommandLatencyMilliseconds.Record(elapsed.TotalMilliseconds);
    }

    public void RecordRuntimeRequestAbandoned() =>
        Interlocked.Increment(ref _runtimeAbandonedRequests);

    public void RecordRuntimeLateResponse() =>
        Interlocked.Increment(ref _runtimeLateResponses);

    public bool RecordSstRead(SstReadSample sample)
    {
        Interlocked.Increment(ref _readsTotal);
        PantsDiagnostics.Reads.Add(1);
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
        PantsDiagnostics.TransactionsConflicted.Add(1);
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
        PantsDiagnostics.CompactionsCompleted.Add(1);
    }

    public void RecordCompactionFailure()
    {
        Interlocked.Increment(ref _compactionFailures);
        PantsDiagnostics.CompactionsFailed.Add(1);
    }

    public void RecordWalAppend(TimeSpan appendElapsed)
    {
        Interlocked.Increment(ref _walAppendCount);
        Interlocked.Add(ref _walAppendNanosecondsTotal, ToNanoseconds(appendElapsed));
        PantsDiagnostics.WalAppends.Add(1);
    }

    public void RecordWalFlush() => Interlocked.Increment(ref _walFlushCount);

    public void RecordDurabilityWaitersFannedOut(int waiterCount) =>
        Interlocked.Add(ref _durabilityWaitersFannedOut, waiterCount);

    public void RecordWalFsyncBoundary(TimeSpan elapsed, long sequence)
    {
        var nanoseconds = ToNanoseconds(elapsed);
        Interlocked.Increment(ref _walFsyncCount);
        Interlocked.Add(ref _walFsyncNanosecondsTotal, nanoseconds);
        SetMaximum(ref _walFsyncNanosecondsMaximum, nanoseconds);
        SetMaximum(ref _walLastSyncedSequence, sequence);
    }

    public void RecordWriteStallNoSpace() => Interlocked.Increment(ref _writeStallsNoSpace);

    public void RecordWriteStallMemory() => Interlocked.Increment(ref _writeStallsMemory);

    public void RecordWriteStallCompaction() => Interlocked.Increment(ref _writeStallsCompaction);

    public void RecordWriteStallCloud() => Interlocked.Increment(ref _writeStallsCloud);

    public WriteStallTimingSnapshot ObserveWriteStall(bool isStalled)
    {
        lock (_writeStallTimingGate)
        {
            var observedAt = _timeProvider.GetTimestamp();
            if (isStalled && _writeStallStartedAt is null)
            {
                _writeStallStartedAt = observedAt;
                _writeStallActiveNanoseconds = 0;
            }
            else if (_writeStallStartedAt is { } startedAt)
            {
                _writeStallActiveNanoseconds = Math.Max(
                    _writeStallActiveNanoseconds,
                    GetElapsedNanoseconds(startedAt, observedAt));
                if (!isStalled)
                {
                    _writeStallNanosecondsTotal = SaturatingAdd(
                        _writeStallNanosecondsTotal,
                        _writeStallActiveNanoseconds);
                    _writeStallNanosecondsMaximum = Math.Max(
                        _writeStallNanosecondsMaximum,
                        _writeStallActiveNanoseconds);
                    _writeStallStartedAt = null;
                    _writeStallActiveNanoseconds = 0;
                }
            }

            var active = _writeStallActiveNanoseconds;
            return new WriteStallTimingSnapshot(
                SaturatingAdd(_writeStallNanosecondsTotal, active),
                Math.Max(_writeStallNanosecondsMaximum, active),
                active);
        }
    }

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
        PantsDiagnostics.CloudWalUploadsCompleted.Add(1);
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
        PantsDiagnostics.FlushPublications.Add(1);
    }

    public void RecordFlushEnqueued() => Interlocked.Increment(ref _flushEnqueued);

    public void RecordFlushRetry() => Interlocked.Increment(ref _flushRetries);

    public void RecordCloudFlushRetry()
    {
        Interlocked.Increment(ref _flushRetries);
        PantsDiagnostics.CloudFlushRetries.Add(1);
    }

    public void RecordFlushFailure() => Interlocked.Increment(ref _flushFailures);

    public void RecordCloudUploadPending() => Interlocked.Increment(ref _pendingCloudUploads);

    public void RecordCloudUploadCompleted() => Interlocked.Decrement(ref _pendingCloudUploads);

    public void RecordSalvageModeOpen() => Interlocked.Increment(ref _salvageModeOpens);

    public void RecordNoSpaceEvent() => Interlocked.Increment(ref _noSpaceEvents);

    public void RecordWalRecovery(int payloadBytes)
    {
        Interlocked.Increment(ref _walRecoveryRecordsReplayed);
        Interlocked.Add(ref _walRecoveryBytesReplayed, payloadBytes);
        PantsDiagnostics.WalRecoveryRecords.Add(1);
    }

    public void RecordIntentLogReplay(int entryCount)
    {
        Interlocked.Increment(ref _intentLogReplayRuns);
        Interlocked.Add(ref _intentLogEntriesReplayed, entryCount);
    }

    public PantsRecoveryMetrics GetRecoveryMetrics() => new(
        WalRecoveryRecordsReplayed,
        WalRecoveryBytesReplayed,
        IntentLogReplayRuns,
        IntentLogEntriesReplayed);

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
        var reads = Volatile.Read(ref _readsTotal);
        var ssts = Volatile.Read(ref _sstsTouchedTotal);
        var l0Ssts = Volatile.Read(ref _l0SstsTouchedTotal);
        var blocks = Volatile.Read(ref _blocksReadTotal);
        var sstBudgetViolations = Volatile.Read(ref _sstBudgetViolations);
        var blockBudgetViolations = Volatile.Read(ref _blockBudgetViolations);
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

    static double Divide(long numerator, long denominator) =>
        denominator == 0 ? 0 : (double)numerator / denominator;

    static long ToNanoseconds(TimeSpan elapsed) =>
        checked((long)(elapsed.TotalMilliseconds * 1_000_000));

    static long ToMicroseconds(TimeSpan elapsed) =>
        checked((long)(elapsed.TotalMilliseconds * 1_000));

    long GetElapsedNanoseconds(long startedAt, long observedAt)
    {
        if (observedAt <= startedAt)
        {
            return 0;
        }

        var ticks = _timeProvider.GetElapsedTime(startedAt, observedAt).Ticks;
        return ticks > long.MaxValue / TimeSpan.NanosecondsPerTick
            ? long.MaxValue
            : ticks * TimeSpan.NanosecondsPerTick;
    }

    static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    static void SetMaximum(ref long target, long value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
