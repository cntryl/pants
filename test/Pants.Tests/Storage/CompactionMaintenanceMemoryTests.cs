using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

/// <summary>
///     Compaction merges and SST publications share the maintenance pool. A publication in flight
///     must make a compaction wait its turn, never fail it with a resource limit.
/// </summary>
public sealed class CompactionMaintenanceMemoryTests
{
    [Fact]
    public async Task ShouldWaitForInFlightWholePoolPublicationInsteadOfFailingCompaction()
    {
        using var directory = new TemporaryDirectory();
        await SeedCompactionInputsAsync(directory.Path);
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        using var store = LocalDiskStore.Open(directory.Path, state);
        var budget = new ResourceBudget(4L * 1024 * 1024);
        var gate = new MaintenanceMemoryGate();
        var publication = await SstPublicationAdmission.Gated(budget, gate)
            .AdmitAsync(64L * 1024 * 1024, CancellationToken.None);

        var compaction = store.CompactAsync(
                state,
                true,
                null,
                false,
                memory: new CompactionMemory(budget, gate))
            .AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.False(compaction.IsCompleted);
        Assert.Equal(budget.Limit, budget.Current);
        publication.Dispose();
        var result = await compaction.WaitAsync(TestTimeouts.Expected);
        Assert.True(result.BytesRewritten > 0);
        Assert.False(result.PersistenceAnomaly);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public async Task ShouldLeaveCompactionInputsUntouchedWhenCanceledWhileWaitingForPublication()
    {
        using var directory = new TemporaryDirectory();
        await SeedCompactionInputsAsync(directory.Path);
        var state = new RuntimeState(new ManualClock(DateTimeOffset.UnixEpoch), new RuntimeTelemetry());
        using var store = LocalDiskStore.Open(directory.Path, state);
        var budget = new ResourceBudget(4L * 1024 * 1024);
        var gate = new MaintenanceMemoryGate();
        var inputs = SstNames(directory.Path);
        using (await SstPublicationAdmission.Gated(budget, gate)
                   .AdmitAsync(64L * 1024 * 1024, CancellationToken.None))
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await store.CompactAsync(
                    state,
                    true,
                    null,
                    false,
                    memory: new CompactionMemory(budget, gate),
                    cancellationToken: cancellation.Token));
        }

        Assert.Equal(inputs, SstNames(directory.Path));

        var result = await store.CompactAsync(
            state,
            true,
            null,
            false,
            memory: new CompactionMemory(budget, gate));

        Assert.True(result.BytesRewritten > 0);
        Assert.Equal(0, budget.Current);
    }

    static async Task SeedCompactionInputsAsync(string root)
    {
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(root).WithBackgroundCompaction(false));
        var family = database.ColumnFamilies.DefaultFamily;
        for (var flush = 0; flush < 4; flush++)
        {
            await using (var transaction = await database.Transactions.BeginAsync(
                             family,
                             PantsTransactionMode.ReadWrite))
            {
                for (var key = 0; key < 16; key++)
                {
                    transaction.Put(TestBytes.FromString($"gate-{flush}-{key:00}"), new byte[1024]);
                }

                await transaction.CommitAsync(PantsWriteOptions.Sync);
            }

            await database.Maintenance.FlushAsync(family);
        }
    }

    static string[] SstNames(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "sst"), "*.sst", SearchOption.TopDirectoryOnly)
            .Select(static path => Path.GetFileName(path))
            .Order(StringComparer.Ordinal)
            .ToArray();
}
