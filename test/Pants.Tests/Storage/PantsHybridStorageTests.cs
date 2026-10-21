using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsHybridStorageTests
{
    const long LocalBudgetBytes = 280 * 1024;
    const long WalAdmissionBudgetBytes = 128 * 1024;
    static readonly ColumnFamilyIdentity DefaultFamily = new(
        0,
        "default",
        RuntimeState.DefaultFamilyVersion);

    [Fact]
    public async Task ShouldApplySimulatedCloudLocalStorageBudgetWhenOpeningSimulatedCloud()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path, 8 * 1024 * 1024);
        await PutAsync(database, "resident", CreateValue(128 * 1024, 11));
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();

        Assert.Equal(8 * 1024 * 1024, metrics.HybridMaximumLocalBytes);
        Assert.True(metrics.HybridTotalCommittedBytes > 0);
        Assert.True(metrics.HybridFreeBytes > 0);
        Assert.True(metrics.HybridUsagePercent > 0);
    }

    [Fact]
    public void ShouldBreakDownHybridStorageChargesWithoutChangingLegacyPhysicalMetrics()
    {
        var store = new SizedHybridCacheStore([new HybridLocalSst("resident.sst", 100)]);
        using var manager = new HybridCacheManager(1000);
        manager.BindStore(store);
        using var wal = manager.Ledger.Reserve(StorageAdmissionKind.Wal, 20);
        using var spill = manager.Ledger.Reserve(StorageAdmissionKind.TransactionSpill, 30);
        using var recovery = manager.Ledger.Reserve(StorageAdmissionKind.RecoverySpool, 40);
        using var hydration = manager.Ledger.Reserve(StorageAdmissionKind.Hydration, 50);
        using var headroom = manager.Ledger.Reserve(StorageAdmissionKind.FlushHeadroom, 60);
        using var flush = manager.Ledger.Reserve(StorageAdmissionKind.Flush, 70);
        using var compaction = manager.Ledger.Reserve(StorageAdmissionKind.Compaction, 80);

        var metrics = manager.GetMetrics(store);
        var storage = metrics.StorageBudgetSnapshot;

        Assert.Equal(100, metrics.TotalCommittedBytes);
        Assert.Equal(450, storage.TotalCommittedBytes);
        Assert.Equal(550, storage.FreeBytes);
        Assert.Equal(45, storage.UsagePercent);
        Assert.Equal(100, storage.Usage.ResidentSstBytes);
        Assert.Equal(30, storage.Usage.TransactionSpillBytes);
        Assert.Equal(60, storage.Usage.FlushHeadroomReservedBytes);
        Assert.Equal(70, storage.Usage.FlushStagingReservedBytes);
        Assert.Equal(80, storage.Usage.CompactionStagingReservedBytes);
        Assert.Equal(20, storage.Usage.WalAppendReservedBytes);
        Assert.Equal(40, storage.Usage.RecoverySpoolReservedBytes);
        Assert.Equal(50, storage.Usage.HydrationReservedBytes);
        Assert.Equal(0, storage.Usage.WalHeadroomReservedBytes);
        Assert.Equal(2, storage.Usage.Reservations);
        Assert.Equal(storage.TotalCommittedBytes, SumLocalStorageCharges(storage.Usage));
    }

    [Fact]
    public async Task ShouldFlushWhenCombinedSmallCloudFamiliesReachTheHybridMemtableThreshold()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions
            .SimulatedCloud(directory.Path, "pants-tests", "combined-memtable-pressure/")
            .WithSimulatedCloudLocalStorageBudget(1024 * 1024)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(32L * 1024 * 1024))
            .WithMemtableLimits(4L * 1024 * 1024);
        await using var database = await PantsDatabase.OpenAsync(options);
        var families = new List<IPantsColumnFamily>();
        for (var index = 0; index < 4; index++)
        {
            families.Add(await database.ColumnFamilies.CreateAsync($"small-{index}"));
        }

        for (var index = 0; index < families.Count; index++)
        {
            await PutAsync(
                database,
                families[index],
                $"key-{index}",
                CreateValue(64 * 1024, 160 + index));
            if (index < families.Count - 1)
            {
                Assert.Empty(LocalSsts(directory.Path));
            }
        }

        Assert.Equal(families.Count, LocalSsts(directory.Path).Length);
        for (var index = 0; index < families.Count; index++)
        {
            await using var reader = await database.Transactions.BeginAsync(
                families[index],
                PantsTransactionMode.ReadOnly);
            Assert.Equal(
                64 * 1024,
                Assert.IsType<ReadOnlyMemory<byte>>(
                    await reader.GetAsync(TestBytes.FromString($"key-{index}"))).Length);
        }
    }

    [Fact]
    public async Task ShouldEvictCloudDurableSstToAdmitNextFlushWindow()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        var value = CreateValue(24 * 1024, 17);
        await PutAsync(database, "large", value);

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

        var firstSst = Assert.Single(LocalSsts(directory.Path));
        Assert.Single(CloudSsts(directory.Path));
        await SeedSstsAsync(database, "seed", 12, value.Length, 18);

        await PutAsync(database, "next-window", CreateValue(value.Length, 19));
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

        Assert.DoesNotContain(firstSst, LocalSsts(directory.Path));
        Assert.True(LocalSsts(directory.Path).Length < CloudSsts(directory.Path).Length);
        Assert.True(CloudSsts(directory.Path).Length >= 12);
        Assert.True((await database.Diagnostics.GetRuntimeMetricsAsync()).HybridFreeBytes > 0);
    }

    [Fact]
    public async Task ShouldEvictAtEmergencyWatermarkDespiteAnActiveSnapshot()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        var value = CreateValue(24 * 1024, 23);
        await PutAsync(database, "pressure", value);
        await using var snapshot = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var pressureSst = Assert.Single(LocalSsts(directory.Path));
        await SeedSstsAsync(database, "snapshot-seed", 12, value.Length, 24);
        await PutAsync(database, "admitted", CreateValue(value.Length, 30));
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

        Assert.DoesNotContain(pressureSst, LocalSsts(directory.Path));
        Assert.True(LocalSsts(directory.Path).Length < CloudSsts(directory.Path).Length);
        Assert.Equal(value, Assert.IsType<ReadOnlyMemory<byte>>(
            await snapshot.GetAsync("pressure"u8.ToArray())).ToArray());
        Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).NoSpaceEvents);
    }

    [Fact]
    public async Task ShouldBlockWritesWhenCloudEvictionContinuesFailing()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new PersistentThrowingFlushFailpointHandler(
            Failpoint.BeforeHybridSstEviction);
        failpoints.Release();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path),
            new RuntimeDependencies(failpoints));
        try
        {
            await SeedSstsAsync(database, "pressure", 12, 24 * 1024, 101);
            failpoints.Arm();
            await Assert.ThrowsAsync<PantsIOException>(() =>
                PutAsync(database, "blocked-window", CreateValue(24 * 1024, 102)).AsTask());
            await using var blocked = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            blocked.Put("blocked"u8.ToArray(), CreateValue(24 * 1024, 104));

            await Assert.ThrowsAsync<PantsIOException>(() =>
                blocked.CommitAsync(PantsWriteOptions.CloudStrict).AsTask());

            Assert.NotEmpty(LocalSsts(directory.Path));
            Assert.True(CloudSsts(directory.Path).Length >= 12);
            Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).NoSpaceEvents);
        }
        finally
        {
            failpoints.Release();
        }
    }

    [Fact]
    public async Task ShouldRejectWalFrameBeforeWritingWhenOutstandingReservationsConsumeHeadroom()
    {
        using var directory = new TemporaryDirectory();
        var state = NewState();
        var firstValue = CreateValue(8 * 1024, 107);
        using var hybridCache = new HybridCacheManager(WalAdmissionBudgetBytes);
        {
            using var store = LocalDiskStore.Open(
                directory.Path,
                state,
                hybridCache: hybridCache);
            using var first = CreateCommitPayload(state, 1, "admitted", firstValue);
            _ = store.AppendCommit(first, state, PantsDurability.Sync);
            var walBytesAfterFirstCommit = WalBytes(directory.Path);

            using (hybridCache.Ledger.Reserve(StorageAdmissionKind.FlushHeadroom, 80 * 1024))
            {
                using var rejected = CreateCommitPayload(
                    state,
                    2,
                    "rejected",
                    CreateValue(20 * 1024, 109));

                Assert.Throws<PantsNoSpaceException>(() => store.AppendCommit(
                    rejected,
                    state,
                    PantsDurability.Sync));

                Assert.Equal(walBytesAfterFirstCommit, WalBytes(directory.Path));
            }

            using var afterRejection = CreateCommitPayload(
                state,
                3,
                "after-rejection",
                "still-writable"u8.ToArray());
            _ = store.AppendCommit(afterRejection, state, PantsDurability.Sync);
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal(
            firstValue,
            Assert.IsType<ReadOnlyMemory<byte>>(await reader.GetAsync("admitted"u8.ToArray())).ToArray());
        Assert.Null(await reader.GetAsync("rejected"u8.ToArray()));
        Assert.Equal(
            "still-writable",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync("after-rejection"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldRejectCoalescedWalAppendBeforeWritingWhenOutstandingReservationsConsumeHeadroom()
    {
        using var directory = new TemporaryDirectory();
        var state = NewState();
        var firstValue = CreateValue(8 * 1024, 113);
        using var hybridCache = new HybridCacheManager(WalAdmissionBudgetBytes);
        {
            using var store = LocalDiskStore.Open(
                directory.Path,
                state,
                hybridCache: hybridCache);
            using var first = CreateCommitPayload(state, 1, "admitted", firstValue);
            _ = store.AppendCommit(first, state, PantsDurability.Sync);
            var walBytesAfterFirstCommit = WalBytes(directory.Path);
            var sequenceAfterFirstCommit = state.Sequence;

            using (hybridCache.Ledger.Reserve(StorageAdmissionKind.FlushHeadroom, 80 * 1024))
            {
                using var firstGrouped = CreateCommitPayload(
                    state,
                    2,
                    "grouped-first",
                    CreateValue(10 * 1024, 127));
                using var secondGrouped = CreateCommitPayload(
                    state,
                    3,
                    "grouped-second",
                    CreateValue(10 * 1024, 131));
                var commits = new[]
                {
                    new WalCommitGroupEntry(firstGrouped, 6),
                    new WalCommitGroupEntry(secondGrouped, 9)
                };

                Assert.Throws<PantsNoSpaceException>(() => store.AppendCommitGroup(
                    commits,
                    state,
                    PantsDurability.Sync,
                    static () => { }));

                Assert.Equal(walBytesAfterFirstCommit, WalBytes(directory.Path));
                Assert.Equal(sequenceAfterFirstCommit, state.Sequence);
            }

            using var afterRejection = CreateCommitPayload(
                state,
                4,
                "after-group-rejection",
                "still-writable"u8.ToArray());
            _ = store.AppendCommit(afterRejection, state, PantsDurability.Sync);
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal(
            firstValue,
            Assert.IsType<ReadOnlyMemory<byte>>(await reader.GetAsync("admitted"u8.ToArray())).ToArray());
        Assert.Null(await reader.GetAsync("grouped-first"u8.ToArray()));
        Assert.Null(await reader.GetAsync("grouped-second"u8.ToArray()));
        Assert.Equal(
            "still-writable",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync("after-group-rejection"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldRejectSpilledTransactionBeforeItsFirstWalFrameWhenHeadroomIsReserved()
    {
        using var directory = new TemporaryDirectory();
        var state = NewState();
        using var hybridCache = new HybridCacheManager(WalAdmissionBudgetBytes);
        {
            using var store = LocalDiskStore.Open(
                directory.Path,
                state,
                hybridCache: hybridCache);
            using var first = CreateCommitPayload(
                state,
                1,
                "admitted",
                CreateValue(8 * 1024, 139));
            _ = store.AppendCommit(first, state, PantsDurability.Sync);
            var walBytesAfterFirstCommit = WalBytes(directory.Path);

            using (hybridCache.Ledger.Reserve(StorageAdmissionKind.FlushHeadroom, 80 * 1024))
            {
                using var spilled = CreateSpilledCommitPayload(
                    directory.Path,
                    state,
                    2,
                    "spilled-rejected",
                    CreateValue(20 * 1024, 149));

                Assert.Throws<PantsNoSpaceException>(() => store.AppendCommit(
                    spilled,
                    state,
                    PantsDurability.Sync));

                Assert.Equal(walBytesAfterFirstCommit, WalBytes(directory.Path));
            }
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        Assert.NotNull(await reader.GetAsync("admitted"u8.ToArray()));
        Assert.Null(await reader.GetAsync("spilled-rejected"u8.ToArray()));
    }

    [Fact]
    public async Task ShouldReserveFlushHeadroomWithWalAndReleaseItAfterSstPublication()
    {
        using var directory = new TemporaryDirectory();
        var state = NewState();
        using var hybridCache = new HybridCacheManager(1024 * 1024);
        var expectedValue = CreateValue(4096, 157);
        {
            using var store = LocalDiskStore.Open(
                directory.Path,
                state,
                hybridCache: hybridCache);
            using var payload = CreateCommitPayload(state, 1, "headroom", expectedValue);

            _ = store.AppendCommit(payload, state, PantsDurability.Sync);

            var claimedBytes = hybridCache.Ledger.ReservedBytes;
            Assert.True(claimedBytes > 0);
            Assert.True(hybridCache.Ledger.ChargedBytes <= 1024 * 1024);
            Assert.Throws<PantsNoSpaceException>(() =>
                hybridCache.Ledger.Reserve(StorageAdmissionKind.Compaction, 1024 * 1024));

            store.Flush(state);

            Assert.Equal(0, hybridCache.Ledger.ReservedBytes);
            Assert.True(store.LocalSstBytes > 0);
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Equal(
            expectedValue,
            Assert.IsType<ReadOnlyMemory<byte>>(await reader.GetAsync("headroom"u8.ToArray())).ToArray());
    }

    [Fact]
    public async Task ShouldEvictEnoughSstsToMeetTheWalCeilingWhenFlushClaimsCrossIt()
    {
        var state = NewState();
        using var payload = CreateCommitPayload(state, 1, "budget", CreateValue(1, 158));
        var footprint = new SstFlushFootprint();
        footprint.Add("budget"u8.Length, 1, 0, false);
        var operationWalBytes = WalCodec.MeasureTransactionBatchOperation("budget"u8.Length, 1, null);
        var walPayloadBytes = checked(WalCodec.TransactionBatchRecordOverhead + operationWalBytes);
        var requiredBytes = checked(
            footprint.MeasureUpperBound(PantsPerformanceGoal.Throughput) +
            WalCodec.MeasureEncodedFrameBytes(checked((int)walPayloadBytes)));
        Assert.InRange(requiredBytes, 1, 899);

        const long maximumLocalBytes = 80_000;
        const long flushHeadroomBytes = 10_000;
        var ceilingBytes = maximumLocalBytes * HybridStorageBudgetPolicy.EmergencyWatermarkPercent / 100;
        var firstSstBytes = Math.Max(1, requiredBytes / 4);
        var secondSstBytes = firstSstBytes;
        var overflowBytes = firstSstBytes + 1;
        var storedSstBytes = checked(
            ceilingBytes - flushHeadroomBytes - requiredBytes + overflowBytes);
        var remainingSstBytes = storedSstBytes - firstSstBytes - secondSstBytes;
        Assert.True(remainingSstBytes > 0);
        var store = new SizedHybridCacheStore(
        [
            new HybridLocalSst("first.sst", firstSstBytes),
            new HybridLocalSst("second.sst", secondSstBytes),
            new HybridLocalSst("remaining.sst", remainingSstBytes)
        ]);
        using var manager = new HybridCacheManager(maximumLocalBytes);
        manager.BindStore(store);
        using var flushHeadroom = manager.Ledger.Reserve(
            StorageAdmissionKind.FlushHeadroom,
            flushHeadroomBytes);

        await manager.EnsureWriteCapacityAsync(
            store,
            payload,
            PantsPerformanceGoal.Throughput,
            PantsDurability.Sync,
            CancellationToken.None);

        Assert.Equal(["first.sst", "second.sst"], store.Evicted);
        Assert.True(manager.Ledger.ChargedBytes + requiredBytes <=
                    manager.Ledger.GetAdmissionCeilingBytes(StorageAdmissionKind.Wal));
    }

    [Fact]
    public async Task ShouldKeepBestEffortWritesBelowTheEmergencyAdmissionCeiling()
    {
        var state = NewState();
        using var payload = CreateCommitPayload(state, 1, "budget", CreateValue(1, 159));
        var footprint = new SstFlushFootprint();
        footprint.Add("budget"u8.Length, 1, 0, false);
        var outputBytes = footprint.MeasureUpperBound(PantsPerformanceGoal.Throughput);
        var store = new SizedHybridCacheStore([]);
        using var manager = new HybridCacheManager(2000);
        manager.BindStore(store);
        using var existingHeadroom = manager.Ledger.Reserve(
            StorageAdmissionKind.FlushHeadroom,
            1960 - outputBytes + 1);

        await Assert.ThrowsAsync<PantsNoSpaceException>(async () =>
            await manager.EnsureWriteCapacityAsync(
                store,
                payload,
                PantsPerformanceGoal.Throughput,
                PantsDurability.BestEffort,
                CancellationToken.None));
    }

    [Fact]
    public void ShouldRetainWalAdmissionWhenFailedAppendHasNoRollbackProof()
    {
        using var directory = new TemporaryDirectory();
        var state = NewState();
        var failpoints = new WalRollbackFailureFailpointHandler();
        using var hybridCache = new HybridCacheManager(1024 * 1024);
        using var store = LocalDiskStore.Open(
            directory.Path,
            state,
            failpoints: failpoints,
            hybridCache: hybridCache);
        using var payload = CreateCommitPayload(
            state,
            1,
            "ambiguous",
            CreateValue(4096, 137));

        Assert.Throws<WalCommitRollbackException>(() => store.AppendCommit(
            payload,
            state,
            PantsDurability.Sync));

        var walBytes = WalBytes(directory.Path);
        Assert.True(walBytes > 0);
        Assert.True(hybridCache.Ledger.ReservedBytes > walBytes);
        Assert.True(hybridCache.Ledger.ChargedBytes >= walBytes * 2);
        Assert.True(store.IsWalFenced);
    }

    [Fact]
    public void ShouldReleaseWalAdmissionAfterAppendRollbackIsDurablyProven()
    {
        using var directory = new TemporaryDirectory();
        var state = NewState();
        var failpoints = new ArmableFailpointHandler();
        using var hybridCache = new HybridCacheManager(1024 * 1024);
        using var store = LocalDiskStore.Open(
            directory.Path,
            state,
            failpoints: failpoints,
            hybridCache: hybridCache);
        using var payload = CreateCommitPayload(
            state,
            1,
            "rolled-back",
            CreateValue(4096, 151));
        failpoints.Arm(Failpoint.AfterWalAppend);

        Assert.Throws<PantsCommitOutcomeUnknownException>(() => store.AppendCommit(
            payload,
            state,
            PantsDurability.Sync));

        Assert.Equal(0, WalBytes(directory.Path));
        Assert.Equal(0, hybridCache.Ledger.ReservedBytes);
        Assert.True(store.IsWalFenced);
    }

    [Fact]
    public async Task ShouldResumeWritesAfterCloudEvictionCompletes()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new PersistentThrowingFlushFailpointHandler(
            Failpoint.BeforeHybridSstEviction);
        failpoints.Release();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path),
            new RuntimeDependencies(failpoints));
        await SeedSstsAsync(database, "pressure", 12, 24 * 1024, 103);
        failpoints.Arm();
        await using (var blocked = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            blocked.Put("blocked"u8.ToArray(), CreateValue(24 * 1024, 105));
            await Assert.ThrowsAsync<PantsIOException>(() =>
                blocked.CommitAsync(PantsWriteOptions.CloudStrict).AsTask());
        }

        failpoints.Release();
        await PutAsync(database, "resumed", CreateValue(24 * 1024, 106));
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        Assert.True(LocalSsts(directory.Path).Length < CloudSsts(directory.Path).Length);
    }

    [Fact]
    public async Task ShouldContinueWritesAfterSnapshotVisibleCacheEviction()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        await PutAsync(database, "pressure", CreateValue(24 * 1024, 29));
        var snapshot = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var pressureSst = Assert.Single(LocalSsts(directory.Path));
        await SeedSstsAsync(database, "visible-seed", 12, 24 * 1024, 30);
        await PutAsync(database, "admitted", CreateValue(24 * 1024, 36));
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        Assert.DoesNotContain(pressureSst, LocalSsts(directory.Path));
        Assert.True(LocalSsts(directory.Path).Length < CloudSsts(directory.Path).Length);

        Assert.Equal(
            24 * 1024,
            Assert.IsType<ReadOnlyMemory<byte>>(
                await snapshot.GetAsync("pressure"u8.ToArray())).Length);
        await snapshot.DisposeAsync();
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        for (var index = 0; index < 5; index++)
        {
            await PutAsync(database, $"resumed-{index}", "value"u8.ToArray());
        }

        Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).NoSpaceEvents);
    }

    [Fact]
    public async Task ShouldPreferLocalReadsBeforeEviction()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path, 8 * 1024 * 1024);
        await PutAsync(database, "cached", "value"u8.ToArray());
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var cloudSst = Assert.Single(CloudSsts(directory.Path));
        File.Delete(cloudSst);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        var value = await reader.GetAsync("cached"u8.ToArray());

        Assert.Equal("value", TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(value)));
        Assert.False(File.Exists(cloudSst));
    }

    [Fact]
    public async Task ShouldFetchFromCloudAfterLocalEviction()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        var value = CreateValue(24 * 1024, 31);
        await PutAsync(database, "evicted", value);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var evictedSst = Assert.Single(LocalSsts(directory.Path));
        await SeedSstsAsync(database, "eviction-seed", 12, value.Length, 32);
        await PutAsync(database, "force-eviction", CreateValue(value.Length, 38));
        Assert.DoesNotContain(evictedSst, LocalSsts(directory.Path));
        Assert.True(LocalSsts(directory.Path).Length < CloudSsts(directory.Path).Length);
        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        var readValue = await reader.GetAsync("evicted"u8.ToArray());

        Assert.Equal(value.Length, Assert.IsType<ReadOnlyMemory<byte>>(readValue).Length);
        Assert.True(LocalSsts(directory.Path).Length < CloudSsts(directory.Path).Length);
    }

    [Fact]
    public async Task ShouldPersistEvictionStateAcrossRestart()
    {
        using var directory = new TemporaryDirectory();
        var firstSst = string.Empty;
        await using (var database = await OpenAsync(directory.Path))
        {
            await PutAsync(database, "persisted", CreateValue(24 * 1024, 37));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            firstSst = Assert.Single(LocalSsts(directory.Path));
            await SeedSstsAsync(database, "persist-seed", 12, 24 * 1024, 38);
            await PutAsync(database, "replacement", CreateValue(24 * 1024, 44));
            Assert.DoesNotContain(firstSst, LocalSsts(directory.Path));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            Assert.True(LocalSsts(directory.Path).Length < CloudSsts(directory.Path).Length);
        }

        await using var reopened = await OpenAsync(directory.Path);

        Assert.True(LocalSsts(directory.Path).Length < CloudSsts(directory.Path).Length);
        Assert.DoesNotContain(firstSst, LocalSsts(directory.Path));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = await reader.GetAsync("persisted"u8.ToArray());
        Assert.Equal(24 * 1024, Assert.IsType<ReadOnlyMemory<byte>>(value).Length);
    }

    [Fact]
    public async Task ShouldHandleCloudUnavailableDuringEviction()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new HybridEvictionFailpointHandler(
            Failpoint.BeforeCloudUpload);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path),
            new RuntimeDependencies(failpoints));
        await PutAsync(database, "retained", CreateValue(24 * 1024, 41));

        await Assert.ThrowsAsync<PantsIOException>(() =>
            database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask());

        Assert.Single(LocalSsts(directory.Path));
        Assert.Empty(CloudSsts(directory.Path));

        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        var storage = Assert.IsType<PantsHybridStorageBudgetSnapshot>(metrics.HybridStorageBudget);
        Assert.True(storage.Usage.ResidentSstBytes > 0);
        Assert.Equal(storage.TotalCommittedBytes, SumLocalStorageCharges(storage.Usage));
        Assert.Null(storage.BlockedAdmission);

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = await reader.GetAsync("retained"u8.ToArray());
        Assert.Equal(24 * 1024, Assert.IsType<ReadOnlyMemory<byte>>(value).Length);
    }

    static long SumLocalStorageCharges(PantsLocalStorageUsage usage) => checked(
        usage.WalBytes +
        usage.TransactionSpillBytes +
        usage.ResidentSstBytes +
        usage.StartupResidueBytes +
        usage.FlushStagingReservedBytes +
        usage.FlushHeadroomReservedBytes +
        usage.CompactionStagingReservedBytes +
        usage.WalHeadroomReservedBytes +
        usage.WalAppendReservedBytes +
        usage.RecoverySpoolReservedBytes +
        usage.HydrationReservedBytes);

    [Fact]
    public async Task ShouldPublishSstBeforeEvictingLocalCacheFile()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new HybridEvictionFailpointHandler(Failpoint.AfterCloudUpload);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path),
            new RuntimeDependencies(failpoints));
        await PutAsync(database, "ordered", CreateValue(24 * 1024, 47));

        await Assert.ThrowsAsync<PantsIOException>(() =>
            database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask());

        Assert.Single(CloudSsts(directory.Path));
        Assert.Single(LocalSsts(directory.Path));
    }

    [Fact]
    public async Task ShouldEvictLocalSstGivenAPreFlushSnapshotCanReadItsMemtableRoot()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        var value = CreateValue(24 * 1024, 43);
        await PutAsync(database, "pinned", value);
        await using var snapshot = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

        var pinnedSst = Assert.Single(LocalSsts(directory.Path));
        await SeedSstsAsync(database, "pre-flush-seed", 12, value.Length, 44);
        await PutAsync(database, "newer", CreateValue(value.Length, 50));
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        Assert.DoesNotContain(pinnedSst, LocalSsts(directory.Path));
        Assert.True(LocalSsts(directory.Path).Length < CloudSsts(directory.Path).Length);
        var snapshotValue = await snapshot.GetAsync("pinned"u8.ToArray());
        Assert.Equal(24 * 1024, Assert.IsType<ReadOnlyMemory<byte>>(snapshotValue).Length);
    }

    [Fact]
    public async Task ShouldEvictSnapshotVisibleSstAndReadItFromRemoteAuthority()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        var value = CreateValue(24 * 1024, 53);
        await PutAsync(database, "pinned", value);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var pinnedSst = Assert.Single(LocalSsts(directory.Path));
        await using var snapshot = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await SeedSstsAsync(database, "remote-seed", 12, value.Length, 59);
        await PutAsync(database, "newer", CreateValue(value.Length, 65));
        Assert.DoesNotContain(pinnedSst, LocalSsts(directory.Path));
        Assert.True(LocalSsts(directory.Path).Length < CloudSsts(directory.Path).Length);

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

        Assert.DoesNotContain(pinnedSst, LocalSsts(directory.Path));
        Assert.True(LocalSsts(directory.Path).Length < CloudSsts(directory.Path).Length);
        Assert.Equal(
            value.Length,
            Assert.IsType<ReadOnlyMemory<byte>>(
                await snapshot.GetAsync("pinned"u8.ToArray())).Length);
        Assert.Null(await snapshot.GetAsync("newer"u8.ToArray()));
    }

    [Fact]
    public async Task ShouldKeepEvictingAndAcceptingWritesGivenUnrelatedCloudResidue()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path);
        var cloudSstDirectory = Path.Combine(directory.Path, "cloud_store", "sst");
        Directory.CreateDirectory(cloudSstDirectory);
        File.WriteAllBytes(Path.Combine(cloudSstDirectory, "malformed.txt"), [1]);

        for (var index = 0; index < 16; index++)
        {
            await PutAsync(database, $"key-{index}", CreateValue(24 * 1024, 61 + index));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        Assert.True(LocalSsts(directory.Path).Length < CloudSsts(directory.Path).Length);
        Assert.True(CloudSsts(directory.Path).Length >= 12);
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(metrics.HybridUsagePercent < 90);
        Assert.Equal(PantsEngineHealth.Degraded, metrics.Health);
    }

    [Fact]
    public async Task ShouldKeepLocalSstGivenCloudCopyDisappearsBeforeEviction()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new DeleteCloudSstBeforeEvictionFailpointHandler(directory.Path);
        failpoints.Disarm();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path),
            new RuntimeDependencies(failpoints));
        await SeedSstsAsync(database, "retained", 12, 24 * 1024, 67);
        failpoints.Arm();
        await Assert.ThrowsAsync<PantsRecoveryFailedException>(() =>
            PutAsync(database, "trigger-eviction", CreateValue(24 * 1024, 79)).AsTask());

        Assert.NotEmpty(LocalSsts(directory.Path));
        Assert.Empty(CloudSsts(directory.Path));
    }

    [Fact]
    public async Task ShouldKeepSnapshotVisibleLocalSstWhileWithinCloudBudget()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenAsync(directory.Path, 280 * 1024);
        await PutAsync(database, "retained", CreateValue(24 * 1024, 73));
        var snapshot = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        Assert.Single(LocalSsts(directory.Path));
        await snapshot.DisposeAsync();
        await using (var transaction = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("maintenance"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.CloudAsync);
        }

        Assert.Single(LocalSsts(directory.Path));
    }

    [Fact]
    public async Task ShouldRejectCloudWalAppendBeforeAnyFrameWhenFlushOutputExceedsMemtableWindow()
    {
        using var directory = new TemporaryDirectory();
        var value = CreateValue(256 * 1024, 151);
        await using (var database = await OpenAsync(directory.Path, WalAdmissionBudgetBytes))
        {
            await Assert.ThrowsAsync<PantsNoSpaceException>(() =>
                PutAsync(database, "large-cloud-transaction", value).AsTask());
            var rejected = await database.Diagnostics.GetRuntimeMetricsAsync();
            var blocked = Assert.IsType<PantsStorageAdmissionBlock>(
                rejected.HybridStorageBudget?.BlockedAdmission);
            Assert.Equal(PantsStorageAdmissionKind.FlushHeadroom, blocked.Operation);
            Assert.Equal(PantsStorageAdmissionReason.LocalCapacity, blocked.Reason);
            Assert.True(blocked.RequestedBytes > blocked.FreeBytesAtRejection);

            await PutAsync(database, "after-rejection", "still-writable"u8.ToArray());
            Assert.Null((await database.Diagnostics.GetRuntimeMetricsAsync())
                .HybridStorageBudget?.BlockedAdmission);
        }

        await using var reopened = await OpenAsync(directory.Path, WalAdmissionBudgetBytes);
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Null(await reader.GetAsync("large-cloud-transaction"u8.ToArray()));
        Assert.Equal(
            "still-writable",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync("after-rejection"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldRejectOversizedSpilledCloudTransactionBeforeWalAppend()
    {
        const long LocalStorageBudgetBytes = 8L * 1024 * 1024;
        const long TransactionMemoryPoolBytes = 64 * 1024;
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path, LocalStorageBudgetBytes)
            .WithTransactionMemoryPool(TransactionMemoryPoolBytes);
        var value = CreateValue(5 * 1024 * 1024, 163);

        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var walBytesBefore = WalBytes(directory.Path);
            await using (var transaction = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Put(TestBytes.FromString("oversized-spilled"), value);
                Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));

                await Assert.ThrowsAsync<PantsNoSpaceException>(() =>
                    transaction.CommitAsync(PantsWriteOptions.CloudStrict).AsTask());
            }

            Assert.Equal(walBytesBefore, WalBytes(directory.Path));
            await PutAsync(database, "after-spilled-rejection", "still-writable"u8.ToArray());
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);

        Assert.Null(await reader.GetAsync("oversized-spilled"u8.ToArray()));
        Assert.Equal(
            "still-writable",
            TestBytes.ToText(Assert.IsType<ReadOnlyMemory<byte>>(
                await reader.GetAsync("after-spilled-rejection"u8.ToArray()))));
    }

    [Fact]
    public async Task ShouldEvictOnlyTheSstsNeededToAdmitAFlushWindow()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new BlockingHybridEvictionFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            CreateOptions(directory.Path, 900 * 1024),
            new RuntimeDependencies(failpoints));
        for (var index = 0; index < 13; index++)
        {
            await PutAsync(database, $"seed-{index}", CreateValue(64 * 1024, 79 + index));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        failpoints.Arm();
        var write = PutAsync(database, "cross-high", CreateValue(96 * 1024, 89)).AsTask();
        var localSstsBeforeAdmission = LocalSsts(directory.Path);
        try
        {
            await failpoints.WaitUntilBlockedAsync(TestTimeouts.Expected);
            Assert.Equal(localSstsBeforeAdmission, LocalSsts(directory.Path));
        }
        finally
        {
            failpoints.Release();
        }

        await write;
        var localSstsAfterAdmission = LocalSsts(directory.Path);
        var retainedPriorSsts = localSstsAfterAdmission
            .Where(localSstsBeforeAdmission.Contains)
            .ToArray();
        Assert.InRange(retainedPriorSsts.Length, 1, localSstsBeforeAdmission.Length - 1);
        Assert.Equal(retainedPriorSsts.Length, localSstsAfterAdmission.Length);

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        Assert.Equal(0, (await database.Diagnostics.GetRuntimeMetricsAsync()).HybridPendingEvictions);
    }

    [Fact]
    public async Task ShouldKeepLocalUsageBoundedAcrossBurstyFlushCycles()
    {
        using var directory = new TemporaryDirectory();

        // This exercises eviction, not write admission. Background compaction stays off so the
        // local/cloud file counts are deterministic, which means nothing drains L0 across the 20
        // cycles below; keep enough budget for each WAL frame while eviction trims resident SSTs.
        await using var database = await PantsDatabase.OpenAsync(
            CreateOptions(directory.Path, 384 * 1024)
                .WithCompaction(new PantsCompactionConfiguration(
                    L0FileCountTrigger: 32,
                    BackgroundEnabled: false)));
        var sawPartialEviction = false;
        for (var index = 0; index < 20; index++)
        {
            await PutAsync(database, $"burst-{index}", CreateValue(32 * 1024, 97 + index));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            var localCount = LocalSsts(directory.Path).Length;
            var cloudCount = CloudSsts(directory.Path).Length;
            var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
            sawPartialEviction |= localCount > 0 && localCount < cloudCount;
            Assert.True(metrics.HybridUsagePercent < 130);
            Assert.Equal(0, metrics.HybridPendingEvictions);
        }

        Assert.True(sawPartialEviction);
    }

    [Fact]
    public async Task ShouldKeepWritesRunningWhenPublishedSstsExceedLocalBudget()
    {
        const long budgetBytes = 1024 * 1024;
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path, budgetBytes)
            .WithCompaction(new PantsCompactionConfiguration(
                L0FileCountTrigger: 32,
                BackgroundEnabled: false));
        await using var database = await PantsDatabase.OpenAsync(options);
        var expectedValues = new List<(string Key, byte[] Value)>();
        var writesContinuedAfterRemoteExceededBudget = false;

        for (var index = 0; index < 12; index++)
        {
            var key = $"budget-key-{index:00}";
            var value = CreateValue(100 * 1024, 211 + index);
            await PutAsync(database, key, value);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

            var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.InRange(metrics.HybridTotalCommittedBytes, 0, budgetBytes);
            Assert.Equal(0, metrics.HybridPendingEvictions);
            Assert.Equal(0, metrics.NoSpaceEvents);
            expectedValues.Add((key, value));

            var remoteBytesAfterFlush = CloudSsts(directory.Path)
                .Sum(static path => new FileInfo(path).Length);
            if (remoteBytesAfterFlush > budgetBytes && index < 11)
            {
                writesContinuedAfterRemoteExceededBudget = true;
            }
        }

        var remoteSsts = CloudSsts(directory.Path);
        var remoteBytes = remoteSsts.Sum(static path => new FileInfo(path).Length);
        Assert.True(remoteBytes > budgetBytes);
        Assert.True(writesContinuedAfterRemoteExceededBudget);
        Assert.True(LocalSsts(directory.Path).Length < remoteSsts.Length);

        await using var reader = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        foreach (var (key, value) in expectedValues)
        {
            Assert.Equal(
                value,
                Assert.IsType<ReadOnlyMemory<byte>>(await reader.GetAsync(TestBytes.FromString(key)))
                    .ToArray());
        }
    }

    static PantsOpenOptions CreateOptions(string path, long budgetBytes = LocalBudgetBytes) =>
        PantsOpenOptions.SimulatedCloud(path, "pants-tests", "hybrid/")
            .WithSimulatedCloudLocalStorageBudget(budgetBytes)
            .WithBackgroundCompaction(false);

    static async Task SeedSstsAsync(
        IPantsDatabase database,
        string keyPrefix,
        int count,
        int valueBytes,
        int seedStart)
    {
        for (var index = 0; index < count; index++)
        {
            await PutAsync(
                database,
                $"{keyPrefix}-{index}",
                CreateValue(valueBytes, seedStart + index));
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }
    }

    static ValueTask<IPantsDatabase> OpenAsync(
        string path,
        long budgetBytes = LocalBudgetBytes) =>
        PantsDatabase.OpenAsync(CreateOptions(path, budgetBytes));

    static async ValueTask PutAsync(
        IPantsDatabase database,
        string key,
        ReadOnlyMemory<byte> value)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), value);
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
    }

    static async ValueTask PutAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key,
        ReadOnlyMemory<byte> value)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), value);
        await transaction.CommitAsync(PantsWriteOptions.CloudStrict);
    }

    static byte[] CreateValue(int length, int seed)
    {
        var value = new byte[length];
        new Random(seed).NextBytes(value);
        return value;
    }

    static string[] LocalSsts(string root) =>
        Directory.GetFiles(Path.Combine(root, "sst"), "*.sst");

    static string[] CloudSsts(string root) =>
        Directory.GetFiles(Path.Combine(root, "cloud_store", "sst"), "*.sst");

    static long WalBytes(string root) =>
        new FileInfo(Path.Combine(root, "wal", "wal.log")).Length;

    static RuntimeState NewState() => new(
        new ManualClock(DateTimeOffset.UnixEpoch),
        new RuntimeTelemetry());

    static CommitPayload CreateCommitPayload(
        RuntimeState state,
        long transactionId,
        string key,
        byte[] value)
    {
        var operation = new TransactionIntentOperation(
            0,
            CommitOperationKind.Put,
            DefaultFamily,
            TestBytes.FromString(key),
            null,
            value,
            null,
            null,
            false);
        var source = new TransactionOperationSource(
            null,
            [operation],
            1,
            DateTimeOffset.UnixEpoch);
        return new CommitPayload(
            transactionId,
            PantsTransactionMode.ReadWrite,
            PantsConflictPolicy.LastWriteWins,
            DateTimeOffset.UnixEpoch,
            state.CreateVersion(),
            source,
            []);
    }

    static CommitPayload CreateSpilledCommitPayload(
        string directory,
        RuntimeState state,
        long transactionId,
        string key,
        byte[] value)
    {
        var spillStore = new TransactionSpillStore(directory, transactionId, DefaultFamily);
        spillStore.WriteRun(
        [
            new TransactionIntentOperation(
                0,
                CommitOperationKind.Put,
                DefaultFamily,
                TestBytes.FromString(key),
                null,
                value,
                null,
                null,
                false)
        ]);
        var source = new TransactionOperationSource(
            spillStore,
            [],
            1,
            DateTimeOffset.UnixEpoch,
            ownsSpillStore: true);
        return new CommitPayload(
            transactionId,
            PantsTransactionMode.ReadWrite,
            PantsConflictPolicy.LastWriteWins,
            DateTimeOffset.UnixEpoch,
            state.CreateVersion(),
            source,
            []);
    }

    sealed class SizedHybridCacheStore(IReadOnlyList<HybridLocalSst> ssts) : IHybridCacheStore
    {
        readonly List<HybridLocalSst> _ssts = [.. ssts];

        public List<string> Evicted { get; } = [];

        public long LocalCommittedBytes => _ssts.Sum(static sst => sst.SizeBytes);

        public long LocalWalBytes => 0;

        public long LocalSstBytes => LocalCommittedBytes;

        public long LocalStartupResidueBytes => 0;

        public IReadOnlyList<HybridLocalSst> GetLocalManifestSsts() => [.. _ssts];

        public bool TryGetManifestSstSizeBytes(string name, out long sizeBytes)
        {
            var sst = _ssts.FirstOrDefault(candidate => candidate.Name == name);
            sizeBytes = sst?.SizeBytes ?? 0;
            return sst is not null;
        }

        public bool IsSstLocal(string name) => _ssts.Any(sst => sst.Name == name);

        public ValueTask VerifyRemoteSstMatchesLocalAsync(
            string name,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public void EvictLocalSst(string name)
        {
            var sst = _ssts.Single(candidate => candidate.Name == name);
            _ssts.Remove(sst);
            Evicted.Add(name);
        }

        public ValueTask HydrateLocalSstAsync(string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
