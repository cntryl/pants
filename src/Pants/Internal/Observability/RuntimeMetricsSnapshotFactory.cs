namespace Pants;

sealed class RuntimeMetricsSnapshotFactory(
    PantsOpenOptions options,
    RuntimeTelemetry telemetry,
    LocalDiskStore? diskStore,
    RuntimeWorker compactionWorker,
    CloudFlushRetryScheduler cloudFlushRetries,
    CloudWalSealController? cloudWalSealController,
    CloudMemtableSegmentTracker? cloudMemtableSegments,
    HybridCacheManager? hybridCache)
{
    public PantsRuntimeMetrics Create(
        PantsRuntimeState state,
        long walCloudDurableSequence)
    {
        var activeSnapshots = state.ActiveSnapshotCount;
        var hybridMetrics = hybridCache is not null && diskStore is not null
            ? hybridCache.GetMetrics(diskStore)
            : null;
        var writeStalled = MemtableWritePressure.IsStalled(options, state);
        var health = diskStore?.GetHealth(state) ?? state.Health;
        return new PantsRuntimeMetrics
        {
            Health = EngineHealthClassifier.Classify(health, writeStalled),
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
            WalCurrentSegmentId = diskStore?.NextWalSequence ?? 0,
            WalPendingWrites = cloudWalSealController?.PendingWrites ?? diskStore?.WalRecords ?? 0,
            WalLastSyncedSequence = telemetry.WalLastSyncedSequence,
            WalLocalDurableSequence = diskStore is null ? 0 : state.Sequence,
            WalCloudDurableSequence = walCloudDurableSequence,
            PendingCompactions = compactionWorker.QueueDepth,
            CompactingSsts = 0,
            ActiveCompactions = compactionWorker.InFlight,
            PendingCloudUploads = telemetry.PendingCloudUploads,
            ActiveSnapshots = activeSnapshots,
            PinnedSsts = activeSnapshots == 0 ? 0 : diskStore?.SstCount ?? 0,
            OldestSnapshotAgeSeconds = GetOldestSnapshotAgeSeconds(state),
            SstCount = diskStore?.SstCount ?? 0,
            SstBytes = diskStore?.SstBytes ?? 0,
            SalvageModeOpens = state.SalvageModeOpens,
            NoSpaceEvents = state.NoSpaceEvents,
            CompactionsRun = telemetry.CompactionsRun,
            CompactionBytesRewritten = telemetry.CompactionBytesRewritten,
            CompactionFailures = compactionWorker.Failures,
            ObsoleteFileBacklog = diskStore?.GetObsoleteFiles().Count ?? 0,
            WriteStallsTotal = checked(
                telemetry.WriteStallsMemoryTotal + telemetry.WriteStallsNoSpaceTotal),
            WriteStallsMemoryTotal = telemetry.WriteStallsMemoryTotal,
            WriteStallsCompactionTotal = 0,
            WriteStallsCloudTotal = 0,
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
            FlushRetriesTotal = checked(
                telemetry.FlushRetriesTotal + cloudFlushRetries.RetryAttempts),
            WriteStallNanosecondsTotal = 0,
            WriteStallNanosecondsMaximum = 0,
            WriteStallActiveNanoseconds = 0,
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
            WalRecoveryRecordsReplayed = diskStore?.WalRecoveryRecordsReplayed ?? 0,
            WalRecoveryBytesReplayed = diskStore?.WalRecoveryBytesReplayed ?? 0,
            IntentLogReplayRuns = state.IntentLogReplayRuns,
            IntentLogEntriesReplayed = state.IntentLogEntriesReplayed
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
