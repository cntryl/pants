using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime.Flush;

public sealed class RecoveredFlushAdmissionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ShouldSettleRejectedRecoveredFlushAdmissionAndPublishTheRetainedGenerationAfterCapacityReturns(
        int publicationFailure)
    {
        using var directory = new TemporaryDirectory();
        var telemetry = new RuntimeTelemetry();
        var original = NewState(telemetry);
        var expected = new byte[16 * 1024];
        new Random(479).NextBytes(expected);
        var family = original.FamilyData.Keys.Single();
        using (var store = LocalDiskStore.Open(directory.Path, original))
        using (var payload = new CommitPayload(
                   1, PantsTransactionMode.ReadWrite, PantsConflictPolicy.LastWriteWins,
                   DateTimeOffset.UnixEpoch, original.CreateVersion(),
                   new TransactionOperationSource(null,
                       [new TransactionIntentOperation(0, CommitOperationKind.Put, family,
                           "retained"u8.ToArray(), null, expected, null, null, false)],
                       1, DateTimeOffset.UnixEpoch), []))
        {
            _ = store.AppendCommit(payload, 0, PantsDurability.Sync);
        }

        using (var cache = new HybridCacheManager(1024 * 1024))
        {
            var recovered = NewState(telemetry);
            var failpoints = new ArmableFailpointHandler();
            using var store = LocalDiskStore.Open(
                directory.Path, recovered, hybridCache: cache, failpoints: failpoints);
            var frozen = Assert.IsType<FrozenMemtableFlush>(store.FreezeMemtable(
                family, recovered.ActiveMemtableBytes[family], checked((ulong)recovered.Sequence)));
            Assert.False(frozen.HasReservedFlushHeadroom);
            var flush = new ImmutableMemtableFlush(frozen);
            var retained = new List<ImmutableMemtableFlush> { flush };
            await using var service = new FlushRuntimeService(1, store, telemetry);
            var pipeline = new ImmutableFlushPipeline(
                telemetry,
                async (generation, plan) =>
                {
                    if (store.RequiresFrozenFlushHeadroom(generation.Id))
                    {
                        var reservation = await cache.ReserveFlushAsync(
                            store, generation.FlushOutputBoundBytes, CancellationToken.None);
                        try
                        {
                            store.RetainFrozenFlushHeadroom(generation, reservation);
                        }
                        catch
                        {
                            reservation.Dispose();
                            throw;
                        }
                    }

                    return await service.ScheduleFrozenFlushAsync(generation, plan);
                },
                (generation, result, failure) =>
                {
                    generation.PublicationPlan ??= result?.PublicationPlan;
                    if (failure is null)
                    {
                        retained.Remove(generation);
                    }

                    generation.CompleteAttempt(failure);
                    return ValueTask.FromResult(false);
                },
                static (_, _) => ValueTask.CompletedTask,
                static () => false);
            using var pressure = cache.Ledger.Reserve(
                StorageAdmissionKind.Compaction,
                cache.Ledger.GetAvailableBytes(StorageAdmissionKind.Compaction));
            var firstAttempt = flush.AttemptTask;

            await pipeline.ScheduleNextAsync(retained, false);

            Assert.IsType<PantsNoSpaceException>(await firstAttempt.WaitAsync(TestTimeouts.Expected));
            Assert.False(flush.IsRunning);
            Assert.True(flush.HasFailed);
            Assert.Null(flush.RunningTask);
            Assert.Same(flush, Assert.Single(retained));
            Assert.Equal(0, service.Enqueued);
            Assert.Equal(0, cache.Ledger.GetReservedBytes(StorageAdmissionKind.Flush));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory.Path, "sst"), "*.sst"));

            pressure.Dispose();
            if (publicationFailure == 1)
            {
                failpoints.Arm(Failpoint.BeforeFlushPublication);
                await pipeline.ScheduleNextAsync(retained, true);
                Assert.IsType<PantsIOException>(await flush.AttemptTask.WaitAsync(TestTimeouts.Expected));
                var staged = Assert.Single(Directory.EnumerateFiles(
                    Path.Combine(directory.Path, "sst", ".flush-staging"), "*.tmp"));
                Assert.True(new FileInfo(staged).Length > 0);
                Assert.True(cache.Ledger.GetReservedBytes(StorageAdmissionKind.Flush) >=
                            new FileInfo(staged).Length);
                Assert.Equal(1, cache.Ledger.GetReservationCount(StorageAdmissionKind.Flush));
                Assert.Same(flush, Assert.Single(retained));

                using var remainingCapacity = cache.Ledger.Reserve(
                    StorageAdmissionKind.Compaction,
                    cache.Ledger.GetAvailableBytes(StorageAdmissionKind.Compaction));
                await pipeline.ScheduleNextAsync(retained, true);
                Assert.Null(await flush.AttemptTask.WaitAsync(TestTimeouts.Expected));
                Assert.Equal(0, cache.Ledger.GetReservedBytes(StorageAdmissionKind.Flush));
                Assert.False(File.Exists(staged));
            }
            else if (publicationFailure == 2)
            {
                failpoints.Arm(Failpoint.AfterFlushManifestPublish);
                await pipeline.ScheduleNextAsync(retained, true);
                Assert.IsType<PantsIOException>(await flush.AttemptTask.WaitAsync(TestTimeouts.Expected));
                Assert.Equal(1, cache.Ledger.GetReservationCount(StorageAdmissionKind.Flush));

                failpoints.Arm(Failpoint.BeforeIntentLogReplace);
                await pipeline.ScheduleNextAsync(retained, true);
                Assert.IsType<PantsIOException>(await flush.AttemptTask.WaitAsync(TestTimeouts.Expected));
                Assert.Equal(0, cache.Ledger.GetReservationCount(StorageAdmissionKind.Flush));
                Assert.Same(flush, Assert.Single(retained));

                using var remainingCapacity = cache.Ledger.Reserve(
                    StorageAdmissionKind.Compaction,
                    cache.Ledger.GetAvailableBytes(StorageAdmissionKind.Compaction));
                await pipeline.ScheduleNextAsync(retained, true);
                Assert.Null(await flush.AttemptTask.WaitAsync(TestTimeouts.Expected));
            }
            else
            {
                await pipeline.ScheduleNextAsync(retained, true);
                Assert.Null(await flush.AttemptTask.WaitAsync(TestTimeouts.Expected));
            }

            Assert.Empty(retained);
            Assert.Equal(publicationFailure + 2, flush.Attempts);
            Assert.Equal(publicationFailure + 1, service.Enqueued);
            Assert.Equal(0, cache.Ledger.ReservedBytes);
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal(expected, (await reader.GetAsync("retained"u8.ToArray()))?.ToArray());
    }

    static RuntimeState NewState(RuntimeTelemetry telemetry) =>
        new(new ManualClock(DateTimeOffset.UnixEpoch), telemetry);
}
