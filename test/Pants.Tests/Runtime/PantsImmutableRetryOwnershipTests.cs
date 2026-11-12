using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsImmutableRetryOwnershipTests
{
    [Fact]
    public async Task ShouldRetryOnlyTheDueFamilyWithoutStealingAnotherFamilysBackoff()
    {
        using var directory = new TemporaryDirectory();
        var time = new ManualSchedulingTimeProvider();
        var failpoints = new PersistentThrowingFlushFailpointHandler(Failpoint.BeforeFlushBuild);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithBackgroundCompaction(false)
                .WithMemtableLimits(256 * 1024, 64 * 1024),
            new RuntimeDependencies(failpoints, runtimeTimeProvider: time));
        var older = database.ColumnFamilies.DefaultFamily;
        var younger = await database.ColumnFamilies.CreateAsync("younger");
        try
        {
            await PutAsync(database, older, "older");
            await WaitForFailuresAsync(database, 1);
            await TestWait.UntilAsync(() => time.HasTimerDueIn(TimeSpan.FromMilliseconds(10)), "first retry timer");

            time.Advance(TimeSpan.FromMilliseconds(10));
            await WaitForFailuresAsync(database, 2);
            await TestWait.UntilAsync(() => time.HasTimerDueIn(TimeSpan.FromMilliseconds(20)), "older 20 ms retry timer");

            await PutAsync(database, younger, "younger");
            await WaitForFailuresAsync(database, 3);
            await TestWait.UntilAsync(() =>
                    time.HasTimerDueIn(TimeSpan.FromMilliseconds(10)) &&
                    time.HasTimerDueIn(TimeSpan.FromMilliseconds(20)),
                "independent family retry timers");

            failpoints.Release();
            time.Advance(TimeSpan.FromMilliseconds(10));
            await TestWait.UntilAsync(async () =>
                {
                    var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
                    return metrics.SstCount == 1 && metrics.ImmutableMemtables == 1;
                },
                "younger family publication at its retry deadline");
            var first = Assert.Single((await database.Diagnostics.GetStorageLayoutAsync())
                .Levels.SelectMany(static level => level.Files));
            Assert.Equal(younger.Id, first.ColumnFamilyId);
            Assert.Equal(1, (await database.Diagnostics.GetRuntimeMetricsAsync()).ImmutableMemtables);

            time.Advance(TimeSpan.FromMilliseconds(10));
            await TestWait.UntilAsync(async () =>
                (await database.Diagnostics.GetRuntimeMetricsAsync()).ImmutableMemtables == 0,
                "older family publication at its own retry deadline");
            Assert.Equal(2, (await database.Diagnostics.GetRuntimeMetricsAsync()).SstCount);
        }
        finally
        {
            failpoints.Release();
        }
    }

    static async Task PutAsync(IPantsDatabase database, IPantsColumnFamily family, string key)
    {
        await using var transaction = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), new byte[64 * 1024]);
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static Task WaitForFailuresAsync(IPantsDatabase database, long count) =>
        TestWait.UntilAsync(async () =>
            (await database.Diagnostics.GetRuntimeMetricsAsync()).FlushFailuresTotal == count,
            $"flush failure {count}");
}
