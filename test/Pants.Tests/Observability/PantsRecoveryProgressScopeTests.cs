using Cntryl.Pants.Recovery;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Observability;

public sealed class PantsRecoveryProgressScopeTests
{
    [Fact]
    public async Task ShouldCreditOnlyTheActiveRecoveryCallerAndPhaseGivenActualWalRecovery()
    {
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();
        await SeedAsync(first.Path);
        await SeedAsync(second.Path);
        var time = new ManualTimeProvider();
        var tracker = new RecoveryProgressTracker(time);
        using var scope = tracker.EnterRecovery();

        await Task.Run(() => RecoverAsync(first.Path));
        Assert.Equal(0, tracker.CompletedEvents);
        await RecoverAsync(first.Path, scope.Record);
        var credited = tracker.CompletedEvents;
        Assert.True(credited > 0);

        time.Advance(TimeSpan.FromSeconds(1));
        using (var nested = tracker.EnterWorkload())
        {
            await RecoverAsync(first.Path, scope.Record);
            await RecoverAsync(first.Path, nested.Record);
            Assert.Equal(credited, tracker.CompletedEvents);
            Assert.Equal(TimeSpan.FromSeconds(1), scope.IdleFor);
        }

        await RecoverAsync(first.Path, scope.Record);
        Assert.True(tracker.CompletedEvents > credited);
        Assert.Equal(TimeSpan.Zero, scope.IdleFor);

        var other = new RecoveryProgressTracker(time);
        using var otherScope = other.EnterRecovery();
        credited = tracker.CompletedEvents;
        await Task.Run(() => RecoverAsync(second.Path, otherScope.Record));
        Assert.True(other.CompletedEvents > 0);
        Assert.Equal(credited, tracker.CompletedEvents);

        tracker.AdvancePhase();
        await RecoverAsync(first.Path, scope.Record);
        Assert.Equal(credited, tracker.CompletedEvents);
        scope.Dispose();
        await RecoverAsync(first.Path, scope.Record);
        Assert.Equal(credited, tracker.CompletedEvents);

        var oldScopeEvents = scope.CompletedEvents;
        time.Advance(TimeSpan.FromSeconds(3));
        using var currentPhase = tracker.EnterRecovery();
        await RecoverAsync(first.Path, currentPhase.Record);
        Assert.True(currentPhase.CompletedEvents > 0);
        Assert.Equal(TimeSpan.Zero, currentPhase.IdleFor);
        Assert.Equal(oldScopeEvents, scope.CompletedEvents);
        Assert.Equal(TimeSpan.FromSeconds(3), scope.IdleFor);
    }

    [Fact]
    public async Task ShouldKeepConcurrentRecoveryObserversIndependentAcrossAsyncContinuations()
    {
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();
        await SeedAsync(first.Path);
        await SeedAsync(second.Path);
        var firstTracker = new RecoveryProgressTracker();
        var secondTracker = new RecoveryProgressTracker();
        using var firstScope = firstTracker.EnterRecovery();
        using var secondScope = secondTracker.EnterRecovery();
        var firstProduced = 0;
        var secondProduced = 0;
        await Task.WhenAll(
            Task.Run(() => RecoverAsync(first.Path, work =>
            {
                Interlocked.Increment(ref firstProduced);
                firstScope.Record(work);
            })),
            Task.Run(() => RecoverAsync(second.Path, work =>
            {
                Interlocked.Increment(ref secondProduced);
                secondScope.Record(work);
            })));

        Assert.True(firstProduced > 0);
        Assert.True(secondProduced > 0);
        Assert.Equal(firstProduced, firstTracker.CompletedEvents);
        Assert.Equal(secondProduced, secondTracker.CompletedEvents);
    }

    [Fact]
    public void ShouldEmitBatchedWorkOnlyAfterAnotherCompletedUnitOrSuccessfulFinish()
    {
        var time = new ManualTimeProvider();
        var completed = new List<RecoveryWorkCompleted>();
        using var sink = new RecoveryWorkSink(completed.Add, time);
        var work = Assert.IsType<RecoveryWorkRecorder>(sink.Begin("test_walk"));
        work.Completed(1);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(completed);
        work.Completed();
        Assert.Empty(completed);
        work.Completed(1);
        Assert.Equal(2, Assert.Single(completed).Bytes);
        work.Completed(1);
        work.Finish();
        Assert.Equal(2, completed.Count);
        Assert.Equal(1, completed[1].Bytes);

        var unfinished = Assert.IsType<RecoveryWorkRecorder>(sink.Begin("unfinished_walk"));
        unfinished.Completed(1);
        sink.Dispose();
        time.Advance(TimeSpan.FromSeconds(10));
        unfinished.Finish();
        Assert.Equal(2, completed.Count);
    }

    static async Task SeedAsync(string path)
    {
        await using var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(path));
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static async Task RecoverAsync(string path, Action<RecoveryWorkCompleted>? completed = null)
    {
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(path), new RuntimeDependencies(recoveryWorkCompleted: completed));
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("value"u8.ToArray(), (await transaction.GetAsync("key"u8.ToArray()))?.ToArray());
    }
}
