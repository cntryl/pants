namespace Cntryl.Pants;

sealed class RuntimeMetricsSnapshotFactory(
    PantsOpenOptions options,
    RuntimeTelemetry telemetry,
    LocalDiskStore? diskStore,
    CompactionRuntimeService compactionWorker,
    CloudWalSealController? cloudWalSealController,
    CloudMemtableSegmentTracker? cloudMemtableSegments,
    HybridCacheManager? hybridCache)
{
    int _storageHealth = (int)PantsEngineHealth.Healthy;

    public PantsRuntimeMetrics Create(
        PantsRuntimeState state,
        long walCloudDurableSequence)
    {
        var hybridMetrics = hybridCache is not null && diskStore is not null
            ? hybridCache.GetMetrics(diskStore)
            : null;
        var health = diskStore?.GetHealth(state) ?? state.Health;
        Volatile.Write(ref _storageHealth, (int)health);
        return RefreshPublished(
            new PantsRuntimeMetrics
            {
                ObsoleteFileBacklog = diskStore?.GetObsoleteFiles().Count ?? 0,
                HybridMaximumLocalBytes = hybridMetrics?.MaximumLocalBytes ?? 0,
                HybridTotalCommittedBytes = hybridMetrics?.TotalCommittedBytes ?? 0,
                HybridFreeBytes = hybridMetrics?.FreeBytes ?? 0,
                HybridUsagePercent = hybridMetrics?.UsagePercent ?? 0,
                HybridPendingEvictions = hybridMetrics?.PendingEvictions ?? 0
            },
            state,
            walCloudDurableSequence);
    }

    public PantsRuntimeMetrics RefreshPublished(
        PantsRuntimeMetrics snapshot,
        PantsRuntimeState state,
        long walCloudDurableSequence)
    {
        if (state.Health != PantsEngineHealth.Healthy)
        {
            Volatile.Write(ref _storageHealth, (int)state.Health);
        }

        var activeSnapshots = state.ActiveSnapshotCount;
        var writeStalled = MemtableWritePressure.IsStalled(options, state);
        var writeStallTiming = telemetry.ObserveWriteStall(writeStalled);
        return snapshot with
        {
            Health = EngineHealthClassifier.Classify(
                (PantsEngineHealth)Volatile.Read(ref _storageHealth),
                writeStalled),
            CurrentSequence = state.Sequence,
            ManifestLastPersistedSequence = diskStore?.LastPersistedSequence ?? 0,
            ManifestNextWalSequence = diskStore?.NextWalSequence ?? 1,
            ActiveMemtables = state.FamilyData.Count,
            ImmutableMemtables = state.ImmutableMemtableFlushes.Count,
            TotalMemtableBytes = MemtableWritePressure.GetTotalBytes(state),
            MemtableSizeLimitBytes = options.MemtableSizeLimitBytes,
            MemtableFlushThresholdBytes = options.MemtableFlushThresholdBytes,
            MaximumMemtableWalSegmentGap = checked((long)(
                cloudMemtableSegments?.MaximumGap(
                    diskStore?.CurrentWalSegmentId ?? 0) ?? 0)),
            WriteStalled = writeStalled,
            WalCurrentSegmentId = checked((long)(diskStore?.CurrentWalSegmentId ?? 0)),
            WalPendingWrites = cloudWalSealController?.PendingWrites ??
                diskStore?.WalPendingWrites ??
                0,
            WalLastSyncedSequence = diskStore?.WalLastSyncedSequence ?? 0,
            WalLocalDurableSequence = diskStore?.WalLocalDurableSequence ?? 0,
            WalCloudDurableSequence = walCloudDurableSequence,
            PendingCompactions = compactionWorker.QueueDepth,
            CompactingSsts = compactionWorker.CompactingSsts,
            ActiveCompactions = compactionWorker.InFlight,
            PendingCloudUploads = telemetry.PendingCloudUploads,
            ActiveSnapshots = activeSnapshots,
            PinnedSsts = activeSnapshots == 0 ? 0 : diskStore?.SstCount ?? 0,
            OldestSnapshotAgeSeconds = GetOldestSnapshotAgeSeconds(state),
            SstCount = diskStore?.SstCount ?? 0,
            SstBytes = diskStore?.SstBytes ?? 0,
            SalvageModeOpens = telemetry.SalvageModeOpens,
            NoSpaceEvents = telemetry.NoSpaceEvents,
            CompactionsRun = telemetry.CompactionsRun,
            CompactionBytesRewritten = telemetry.CompactionBytesRewritten,
            CompactionFailures = telemetry.CompactionFailures,
            WriteStallsTotal = checked(
                telemetry.WriteStallsMemoryTotal +
                telemetry.WriteStallsCompactionTotal +
                telemetry.WriteStallsCloudTotal +
                telemetry.WriteStallsNoSpaceTotal),
            WriteStallsMemoryTotal = telemetry.WriteStallsMemoryTotal,
            WriteStallsCompactionTotal = telemetry.WriteStallsCompactionTotal,
            WriteStallsCloudTotal = telemetry.WriteStallsCloudTotal,
            WriteStallsNoSpaceTotal = telemetry.WriteStallsNoSpaceTotal,
            WriteConflictsTotal = checked(
                telemetry.WriteConflictsPointTotal + telemetry.WriteConflictsRangeTotal),
            WriteConflictsPointTotal = telemetry.WriteConflictsPointTotal,
            WriteConflictsRangeTotal = telemetry.WriteConflictsRangeTotal,
            CacheHits = telemetry.CacheHits,
            CacheMisses = telemetry.CacheMisses,
            WalAppendCount = telemetry.WalAppendCount,
            WalFlushCount = telemetry.WalFlushCount,
            WalFsyncCount = telemetry.WalFsyncCount,
            WalAppendNanosecondsTotal = telemetry.WalAppendNanosecondsTotal,
            WalFsyncNanosecondsTotal = telemetry.WalFsyncNanosecondsTotal,
            WalFsyncNanosecondsMaximum = telemetry.WalFsyncNanosecondsMaximum,
            DurabilityWaitersFannedOutTotal = telemetry.DurabilityWaitersFannedOut,
            SstBloomRejectsTotal = telemetry.SstBloomRejects,
            SstBloomChecksTotal = telemetry.SstBloomChecks,
            SstBloomTruePositivesTotal = telemetry.SstBloomTruePositives,
            SstBloomFalsePositivesTotal = telemetry.SstBloomFalsePositives,
            SstKeyRangeRejectsTotal = telemetry.SstKeyRangeRejects,
            SstDataBlocksReadTotal = telemetry.SstDataBlocksRead,
            ReadAmplificationCompactionTriggersTotal =
                telemetry.ReadAmplificationCompactionTriggers,
            FlushQueueDepth = state.ImmutableMemtableFlushes.Values.Count(
                static flush => !flush.IsRunning),
            FlushInFlight = state.ImmutableMemtableFlushes.Values.Count(
                static flush => flush.IsRunning),
            FlushEnqueuedTotal = telemetry.FlushEnqueuedTotal,
            FlushBuildCount = telemetry.FlushBuildCount,
            FlushBuildNanosecondsTotal = telemetry.FlushBuildNanosecondsTotal,
            FlushBuildNanosecondsMaximum = telemetry.FlushBuildNanosecondsMaximum,
            FlushPublishCount = telemetry.FlushPublishCount,
            FlushPublishNanosecondsTotal = telemetry.FlushPublishNanosecondsTotal,
            FlushPublishNanosecondsMaximum = telemetry.FlushPublishNanosecondsMaximum,
            FlushFailuresTotal = telemetry.FlushFailuresTotal,
            FlushRetriesTotal = telemetry.FlushRetriesTotal,
            WriteStallNanosecondsTotal = writeStallTiming.TotalNanoseconds,
            WriteStallNanosecondsMaximum = writeStallTiming.MaximumNanoseconds,
            WriteStallActiveNanoseconds = writeStallTiming.ActiveNanoseconds,
            CloudAsyncWalSegmentsSealed = telemetry.CloudAsyncWalSegmentsSealed,
            CloudAsyncWalBytesSealed = telemetry.CloudAsyncWalBytesSealed,
            CloudAsyncWalSealLatencyMicroseconds = telemetry.CloudAsyncWalSealLatencyMicroseconds,
            CloudAsyncWalUploadsStarted = telemetry.CloudAsyncWalUploadsStarted,
            CloudAsyncWalUploadsCompleted = telemetry.CloudAsyncWalUploadsCompleted,
            CloudAsyncWalUploadsFailed = telemetry.CloudAsyncWalUploadsFailed,
            CloudAsyncWalUploadLatencyMicroseconds =
                telemetry.CloudAsyncWalUploadLatencyMicroseconds,
            CloudAsyncWalAcknowledgementLatencyMicroseconds =
                telemetry.CloudAsyncWalAcknowledgementLatencyMicroseconds,
            HybridPendingEvictions = hybridCache?.PendingEvictions ?? 0,
            WalRecoveryRecordsReplayed = telemetry.WalRecoveryRecordsReplayed,
            WalRecoveryBytesReplayed = telemetry.WalRecoveryBytesReplayed,
            IntentLogReplayRuns = telemetry.IntentLogReplayRuns,
            IntentLogEntriesReplayed = telemetry.IntentLogEntriesReplayed
        };
    }

    public PantsRuntimeMetrics RefreshLive(
        PantsRuntimeMetrics snapshot,
        int activeCompactionRequests,
        long walCloudDurableSequence)
    {
        var hybridMetrics = hybridCache is not null && diskStore is not null
            ? hybridCache.GetMetrics(diskStore)
            : null;
        var activeCompactions = compactionWorker.InFlight;
        var writeStallTiming = telemetry.ObserveWriteStall(snapshot.WriteStalled);
        return snapshot with
        {
            PendingCompactions = Math.Max(
                compactionWorker.QueueDepth,
                Math.Max(0, activeCompactionRequests - activeCompactions)),
            CompactingSsts = compactionWorker.CompactingSsts,
            ActiveCompactions = activeCompactions,
            WalCloudDurableSequence = walCloudDurableSequence,
            PendingCloudUploads = telemetry.PendingCloudUploads,
            SalvageModeOpens = telemetry.SalvageModeOpens,
            NoSpaceEvents = telemetry.NoSpaceEvents,
            CompactionFailures = telemetry.CompactionFailures,
            WriteStallsTotal = checked(
                telemetry.WriteStallsMemoryTotal +
                telemetry.WriteStallsCompactionTotal +
                telemetry.WriteStallsCloudTotal +
                telemetry.WriteStallsNoSpaceTotal),
            WriteStallsMemoryTotal = telemetry.WriteStallsMemoryTotal,
            WriteStallsCompactionTotal = telemetry.WriteStallsCompactionTotal,
            WriteStallsCloudTotal = telemetry.WriteStallsCloudTotal,
            WriteStallsNoSpaceTotal = telemetry.WriteStallsNoSpaceTotal,
            WriteStallNanosecondsTotal = writeStallTiming.TotalNanoseconds,
            WriteStallNanosecondsMaximum = writeStallTiming.MaximumNanoseconds,
            WriteStallActiveNanoseconds = writeStallTiming.ActiveNanoseconds,
            FlushFailuresTotal = telemetry.FlushFailuresTotal,
            FlushRetriesTotal = telemetry.FlushRetriesTotal,
            CloudAsyncWalSegmentsSealed = telemetry.CloudAsyncWalSegmentsSealed,
            CloudAsyncWalBytesSealed = telemetry.CloudAsyncWalBytesSealed,
            CloudAsyncWalSealLatencyMicroseconds = telemetry.CloudAsyncWalSealLatencyMicroseconds,
            CloudAsyncWalUploadsStarted = telemetry.CloudAsyncWalUploadsStarted,
            CloudAsyncWalUploadsCompleted = telemetry.CloudAsyncWalUploadsCompleted,
            CloudAsyncWalUploadsFailed = telemetry.CloudAsyncWalUploadsFailed,
            CloudAsyncWalUploadLatencyMicroseconds =
                telemetry.CloudAsyncWalUploadLatencyMicroseconds,
            CloudAsyncWalAcknowledgementLatencyMicroseconds =
                telemetry.CloudAsyncWalAcknowledgementLatencyMicroseconds,
            HybridMaximumLocalBytes = hybridMetrics?.MaximumLocalBytes ?? 0,
            HybridTotalCommittedBytes = hybridMetrics?.TotalCommittedBytes ?? 0,
            HybridFreeBytes = hybridMetrics?.FreeBytes ?? 0,
            HybridUsagePercent = hybridMetrics?.UsagePercent ?? 0,
            HybridPendingEvictions = hybridMetrics?.PendingEvictions ?? 0,
            WalRecoveryRecordsReplayed = telemetry.WalRecoveryRecordsReplayed,
            WalRecoveryBytesReplayed = telemetry.WalRecoveryBytesReplayed,
            IntentLogReplayRuns = telemetry.IntentLogReplayRuns,
            IntentLogEntriesReplayed = telemetry.IntentLogEntriesReplayed
        };
    }

    static long GetOldestSnapshotAgeSeconds(PantsRuntimeState state) =>
        state.ActiveSnapshotCount == 0
            ? 0
            : checked((long)state.ActiveSnapshots
                .Max(snapshot => GetSnapshotAge(
                    state.Clock.UtcNow,
                    snapshot.StartedAtUtc).TotalSeconds));

    static TimeSpan GetSnapshotAge(DateTimeOffset now, DateTimeOffset startedAtUtc) =>
        now <= startedAtUtc ? TimeSpan.Zero : now - startedAtUtc;
}
