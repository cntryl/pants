using Cntryl.Pants.Storage.Internal.Accounting;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Accounting;

public sealed class MetadataAccountingEngineTests
{
    [Fact]
    public async Task ShouldInvalidateUninstalledAcceptedPublicationAfterActualOwnedDisposal()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false)
            .WithMemtableLimits(4 * 1024 * 1024);
        var value = new byte[128 * 1024];
        await using (var seed = await PantsDatabase.OpenAsync(options))
        {
            await using var transaction = await seed.Transactions.BeginAsync(seed.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
            transaction.Put("row"u8.ToArray(), value);
            await transaction.CommitAsync(PantsWriteOptions.Sync);
            await seed.ShutdownAsync(TestTimeouts.Expected);
        }

        using var publication = new FlushPipelineFailpointHandler(Failpoint.BeforeFlushPublication);
        var actor = await Actor.OpenAsync(RuntimePlan.Resolve(options.WithMemtableLimits(256 * 1024, 64 * 1024)),
            new NonDecreasingPantsClock(options.TtlClock), new RuntimeTelemetry(), new RuntimeDependencies(publication));
        var handle = actor.MetadataAccounting;
        Task? disposal = null;
        try
        {
            await publication.WaitUntilEnteredAsync(TestTimeouts.Expected);
            disposal = actor.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            await Assert.ThrowsAsync<PantsLeaseHeldException>(() => PantsDatabase.OpenAsync(options).AsTask());
        }
        finally
        {
            publication.Release();
            await (disposal ?? actor.DisposeAsync().AsTask()).WaitAsync(TestTimeouts.Expected);
        }

        var settled = handle.Snapshot();
        Assert.True(settled.IncompleteObservations > 0);
        Assert.All(settled.Buckets, static bucket => Assert.Equal(0UL, bucket.ActiveOperations));
        Assert.Equal(0UL, settled.Bucket(MetadataOrigin.Recovery, MetadataMedium.Persistent)[MetadataCounter.FlushCommittedCount]);
        await using var recovered = await PantsDatabase.OpenAsync(options);
        await using var read = await recovered.Transactions.BeginAsync(recovered.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal(value, (await read.GetAsync("row"u8.ToArray()))?.ToArray());
    }

    [Fact]
    public async Task ShouldBeginPublicationClockAfterActualInitialBuildCompletes()
    {
        using var directory = new TemporaryDirectory();
        var time = new ManualTimeProvider();
        using var build = new FlushPipelineFailpointHandler(Failpoint.BeforeFlushBuild);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false),
            new RuntimeDependencies(build, runtimeTimeProvider: time));
        var handle = ((DatabaseInstance)database).MetadataAccounting;
        await PutAsync(database);
        var flush = database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask();
        try
        {
            await build.WaitUntilEnteredAsync(TestTimeouts.Expected);
            time.Advance(TimeSpan.FromSeconds(3));
        }
        finally
        {
            build.Release();
        }

        await flush.WaitAsync(TestTimeouts.Expected);
        var bucket = handle.Snapshot().Bucket(MetadataOrigin.OrdinaryLocalFlush, MetadataMedium.Persistent);
        Assert.Equal(1UL, bucket[MetadataCounter.PublicationAttempts]);
        Assert.Equal(1UL, bucket[MetadataCounter.FlushCommittedCount]);
        Assert.Equal(0UL, bucket[MetadataCounter.FullPublicationNanoseconds]);
    }

    [Fact]
    public async Task ShouldAttributeActualDdlAndCompactionWithoutOrdinaryFlushCredit()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithCompaction(new PantsCompactionConfiguration(L0FileCountTrigger: 2,
                L1TargetSizeBytes: 1, MaximumLevels: 3, BackgroundEnabled: false));
        await using var database = await PantsDatabase.OpenAsync(options);
        var handle = ((DatabaseInstance)database).MetadataAccounting;
        var before = handle.Snapshot();
        var disposableFamily = await database.ColumnFamilies.CreateAsync("ddl");
        await database.ColumnFamilies.DropAsync(disposableFamily);
        var ddl = handle.Snapshot().Delta(before).Bucket(MetadataOrigin.Ddl, MetadataMedium.Persistent);
        Assert.True(ddl[MetadataCounter.JournalDurableBytes] > 0);
        Assert.True(ddl[MetadataCounter.CheckpointCompleteCount] > 0);
        Assert.Equal(0UL, ddl[MetadataCounter.FlushCommittedCount]);

        for (var index = 0; index < 2; index++)
        {
            await PutAsync(database);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        before = handle.Snapshot();
        await database.Maintenance.CompactAllAsync();
        var delta = handle.Snapshot().Delta(before);
        var compaction = delta.Bucket(MetadataOrigin.CompactionBeforeGc, MetadataMedium.Persistent);
        Assert.True(compaction[MetadataCounter.JournalDurableBytes] > 0);
        Assert.Equal(1UL, compaction[MetadataCounter.CompactionCommittedCount]);
        Assert.True(compaction[MetadataCounter.CompactionCommittedSstBytes] > 0);
        Assert.Equal(0UL, delta.Bucket(MetadataOrigin.OrdinaryLocalFlush, MetadataMedium.Persistent)[MetadataCounter.FlushCommittedCount]);
        await database.ShutdownAsync(TestTimeouts.Expected);
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var read = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("value"u8.ToArray(), (await read.GetAsync("row"u8.ToArray()))?.ToArray());
    }

    [Fact]
    public async Task ShouldAttributeRealStreamingWalCheckpointsToRecoveryAndRetainExactRows()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(32 * 1024 * 1024))
            .WithMemtableLimits(4 * 1024 * 1024)
            .WithBackgroundCompaction(false);
        await using (var seed = await PantsDatabase.OpenAsync(options))
        {
            for (var index = 0; index < 16; index++)
            {
                await using var transaction = await seed.Transactions.BeginAsync(seed.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
                transaction.Put(BitConverter.GetBytes(index), Enumerable.Repeat((byte)index, 16 * 1024).ToArray());
                await transaction.CommitAsync(PantsWriteOptions.Sync);
            }

            Assert.Equal(0, (await seed.Diagnostics.GetRuntimeMetricsAsync()).SstCount);
            await seed.ShutdownAsync(TestTimeouts.Expected);
        }

        await using var recovered = await PantsDatabase.OpenAsync(options.WithMemtableLimits(128 * 1024));
        var snapshot = ((DatabaseInstance)recovered).MetadataAccounting.Snapshot();
        var recovery = snapshot.Bucket(MetadataOrigin.Recovery, MetadataMedium.Persistent);
        Assert.True(recovery[MetadataCounter.FlushCommittedCount] > 0);
        Assert.True(recovery[MetadataCounter.FlushCommittedSstBytes] > 0);
        Assert.Equal(0UL, snapshot.Bucket(MetadataOrigin.OrdinaryLocalFlush, MetadataMedium.Persistent)[MetadataCounter.FlushCommittedCount]);
        Assert.Equal(0UL, snapshot.IncompleteObservations);
        await using var read = await recovered.Transactions.BeginAsync(recovered.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        for (var index = 0; index < 16; index++)
        {
            Assert.Equal(Enumerable.Repeat((byte)index, 16 * 1024).ToArray(),
                (await read.GetAsync(BitConverter.GetBytes(index)))?.ToArray());
        }
    }

    [Fact]
    public async Task ShouldDistinguishDurableSnapshotFromFailedCheckpointAndRecoverExactValue()
    {
        using var directory = new TemporaryDirectory();
        var failpoint = new ArmableFailpointHandler();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(failpoint));
        var handle = ((DatabaseInstance)database).MetadataAccounting;
        await PutAsync(database);
        var before = handle.Snapshot();
        failpoint.Arm(Failpoint.AfterManifestSnapshotDurable);

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);

        var delta = handle.Snapshot().Delta(before);
        var bucket = delta.Bucket(MetadataOrigin.OrdinaryLocalFlush, MetadataMedium.Persistent);
        Assert.True(bucket[MetadataCounter.SnapshotDurableBytes] > 0);
        Assert.Equal(bucket[MetadataCounter.SnapshotIssuedBytes], bucket[MetadataCounter.SnapshotReturnedBytes]);
        Assert.Equal(bucket[MetadataCounter.SnapshotDurableCount], bucket[MetadataCounter.CheckpointCompleteCount] + 1);
        Assert.Equal(1UL, bucket[MetadataCounter.OperationFailures]);
        await database.ShutdownAsync(TestTimeouts.Expected);
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var read = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("value"u8.ToArray(), (await read.GetAsync("row"u8.ToArray()))?.ToArray());
    }

    [Fact]
    public async Task ShouldRetainCloudFlushOriginAndClockWhenShutdownRetriesFailedMirror()
    {
        using var directory = new TemporaryDirectory();
        var time = new ManualTimeProvider();
        var failure = new PersistentThrowingFlushFailpointHandler(Failpoint.BeforeCloudUpload);
        failure.Release();
        var options = PantsOpenOptions.SimulatedCloud(directory.Path, "accounting", "retry/")
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenForTestingAsync(options,
            new RuntimeDependencies(failure, runtimeTimeProvider: time));
        var handle = ((DatabaseInstance)database).MetadataAccounting;
        await PutAsync(database, PantsWriteOptions.CloudStrict);
        failure.Arm();
        try
        {
            await Assert.ThrowsAsync<PantsIOException>(() => database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask());
            Assert.Equal(0UL, handle.Snapshot().Bucket(MetadataOrigin.CloudFlush, MetadataMedium.Persistent)[MetadataCounter.FlushCommittedCount]);
            time.Advance(TimeSpan.FromSeconds(2));
        }
        finally
        {
            failure.Release();
        }

        await database.ShutdownAsync(TestTimeouts.Expected);
        var settled = handle.Snapshot();
        var cloud = settled.Bucket(MetadataOrigin.CloudFlush, MetadataMedium.Persistent);
        Assert.Equal(1UL, cloud[MetadataCounter.FlushCommittedCount]);
        Assert.Equal(2_000_000_000UL, cloud[MetadataCounter.FullPublicationNanoseconds]);
        Assert.Equal(0UL, settled.Bucket(MetadataOrigin.Shutdown, MetadataMedium.Persistent)[MetadataCounter.FlushCommittedCount]);
        Assert.Equal(0UL, settled.IncompleteObservations);
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var read = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("value"u8.ToArray(), (await read.GetAsync("row"u8.ToArray()))?.ToArray());
    }

    [Fact]
    public async Task ShouldAttributeNewForcedCloudPublicationToShutdown()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.SimulatedCloud(directory.Path, "accounting", "shutdown/")
            .WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenAsync(options);
        var handle = ((DatabaseInstance)database).MetadataAccounting;
        await PutAsync(database, PantsWriteOptions.CloudStrict);
        await database.ShutdownAsync(TestTimeouts.Expected);
        var settled = handle.Snapshot();
        Assert.Equal(1UL, settled.Bucket(MetadataOrigin.Shutdown, MetadataMedium.Persistent)[MetadataCounter.FlushCommittedCount]);
        Assert.Equal(0UL, settled.Bucket(MetadataOrigin.CloudFlush, MetadataMedium.Persistent)[MetadataCounter.FlushCommittedCount]);
        Assert.All(settled.Buckets, static bucket => Assert.Equal(0UL, bucket.ActiveOperations));
    }

    [Fact]
    public async Task ShouldRetainOnlyOriginalOwnerCountersAfterActualShutdownAndReopen()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        MetadataAccountingHandle retained;
        MetadataAccountingSnapshot settled;
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            retained = ((DatabaseInstance)database).MetadataAccounting;
            await PutAsync(database);
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            var flushed = retained.Snapshot();
            var bucket = flushed.Bucket(MetadataOrigin.OrdinaryLocalFlush, MetadataMedium.Persistent);
            Assert.Equal(1UL, bucket[MetadataCounter.FlushCommittedCount]);
            Assert.True(bucket[MetadataCounter.FlushCommittedSstBytes] > 0);
            Assert.True(bucket[MetadataCounter.JournalDurableBytes] > 0);
            Assert.True(flushed.Bucket(MetadataOrigin.Recovery, MetadataMedium.Persistent)[MetadataCounter.CheckpointCompleteCount] > 0);
            await database.ShutdownAsync(TestTimeouts.Expected);
            settled = retained.Snapshot();
            Assert.All(settled.Buckets, static item => Assert.Equal(0UL, item.ActiveOperations));
        }

        await using (var reopened = await PantsDatabase.OpenAsync(options))
        {
            var next = ((DatabaseInstance)reopened).MetadataAccounting.Snapshot();
            Assert.NotEqual(settled.OwnerId, next.OwnerId);
            Assert.Throws<InvalidOperationException>(() => next.Delta(settled));
            await using var read = await reopened.Transactions.BeginAsync(reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
            Assert.Equal("value"u8.ToArray(), (await read.GetAsync("row"u8.ToArray()))?.ToArray());
        }

        Assert.All(retained.Snapshot().Delta(settled).Buckets,
            static bucket => Assert.All(bucket.Counters, static count => Assert.Equal(0UL, count)));
    }

    [Fact]
    public async Task ShouldKeepAcceptedOrdinaryOriginAndClockAcrossPublicationRetry()
    {
        using var directory = new TemporaryDirectory();
        var time = new ManualTimeProvider();
        var failure = new PersistentThrowingFlushFailpointHandler(Failpoint.BeforeFlushManifestPublish);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false),
            new RuntimeDependencies(failure, runtimeTimeProvider: time));
        var handle = ((DatabaseInstance)database).MetadataAccounting;
        await PutAsync(database);
        var flush = database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask();
        try
        {
            await failure.WaitUntilEnteredAsync(TestTimeouts.Expected);
            await Assert.ThrowsAsync<PantsIOException>(() => flush);
            Assert.Equal(0UL, handle.Snapshot().Bucket(MetadataOrigin.OrdinaryLocalFlush, MetadataMedium.Persistent)[MetadataCounter.FlushCommittedCount]);
            time.Advance(TimeSpan.FromSeconds(2));
        }
        finally
        {
            failure.Release();
        }

        await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        var snapshot = handle.Snapshot();
        var bucket = snapshot.Bucket(MetadataOrigin.OrdinaryLocalFlush, MetadataMedium.Persistent);
        Assert.Equal(1UL, bucket[MetadataCounter.FlushCommittedCount]);
        Assert.Equal(2_000_000_000UL, bucket[MetadataCounter.FullPublicationNanoseconds]);
        Assert.True(bucket[MetadataCounter.PublicationFailures] > 0);
        Assert.True(bucket[MetadataCounter.PublicationAttempts] >= 2);
        Assert.Equal(0UL, snapshot.Bucket(MetadataOrigin.Shutdown, MetadataMedium.Persistent)[MetadataCounter.FlushCommittedCount]);
    }

    static async Task PutAsync(IPantsDatabase database, PantsWriteOptions? options = null)
    {
        await using var transaction = await database.Transactions.BeginAsync(database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        transaction.Put("row"u8.ToArray(), "value"u8.ToArray());
        await transaction.CommitAsync(options ?? PantsWriteOptions.Sync);
    }
}
