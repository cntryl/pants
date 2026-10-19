using System.Security.Cryptography;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsFlushWorkingMemoryTests
{
    [Fact]
    public async Task ShouldFlushMemtableAtItsLimitWithinDerivedWorkingMemoryAllowanceAndReopen()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(8L * 1024 * 1024))
            .WithMemtableLimits(2L * 1024 * 1024);
        var plan = RuntimePlan.Resolve(options);
        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            new RuntimeTelemetry());
        var key = TestBytes.FromString("at-limit");
        var value = RandomNumberGenerator.GetBytes(
            checked((int)plan.MemtableSizeLimitBytes - key.Length - 64));
        using (var store = LocalDiskStore.Open(
                   directory.Path,
                   state,
                   flushWorkingMemoryLimitBytes: plan.FlushWorkingMemoryLimitBytes))
        {
            var family = new ColumnFamilyIdentity(
                0,
                "default",
                RuntimeState.DefaultFamilyVersion);
            var operation = new TransactionIntentOperation(
                0,
                CommitOperationKind.Put,
                family,
                key,
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
            using var payload = new CommitPayload(
                1,
                PantsTransactionMode.ReadWrite,
                PantsConflictPolicy.LastWriteWins,
                DateTimeOffset.UnixEpoch,
                state.CreateVersion(),
                source,
                []);

            _ = store.AppendCommit(payload, state, PantsDurability.Sync);
            store.Flush(state);

            Assert.Equal(plan.FlushWorkingMemoryLimitBytes, store.FlushWorkingMemoryLimitBytes);
            Assert.InRange(store.FlushWorkingMemoryPeakBytes, 1, plan.FlushWorkingMemoryLimitBytes);
        }

        await using var reopened = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        await using var transaction = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var valueAfterReopen = await transaction.GetAsync(TestBytes.FromString("at-limit"));

        Assert.NotNull(valueAfterReopen);
        Assert.Equal(value, valueAfterReopen!.Value.ToArray());
    }
}
