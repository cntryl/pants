using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsLocalDdlAuthorityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ShouldReportUnknownDdlOutcomeAfterAdmissionAndReconcileCommittedCatalog(bool drop, bool cancel)
    {
        using var directory = new TemporaryDirectory();
        using var barrier = new JournalBarrier();
        using var cancellation = new CancellationTokenSource();
        var time = new ManualSchedulingTimeProvider();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithStorageTimeout(TimeSpan.FromSeconds(1))
            .WithRuntimeResponseTimeout(TimeSpan.FromSeconds(10));
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         options, new RuntimeDependencies(barrier, runtimeTimeProvider: time)))
        {
            var family = drop ? await database.ColumnFamilies.CreateAsync("changed") : null;
            barrier.Arm();
            Task operation = drop
                ? database.ColumnFamilies.DropAsync(family!, cancellation.Token).AsTask()
                : database.ColumnFamilies.CreateAsync("changed", cancellation.Token).AsTask();
            try
            {
                await barrier.Entered.WaitAsync(TestTimeouts.Expected);
                if (cancel)
                {
                    cancellation.Cancel();
                }
                else
                {
                    time.Advance(TimeSpan.FromSeconds(11));
                }
                await Assert.ThrowsAsync<PantsCommitOutcomeUnknownException>(() => operation);
            }
            finally
            {
                barrier.Release();
            }

            Assert.Equal(!drop, await database.ColumnFamilies.GetAsync("changed") is not null);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.Equal(!drop, await reopened.ColumnFamilies.GetAsync("changed") is not null);
    }

    [Theory]
    [InlineData(false, "AfterManifestJournalAppend")]
    [InlineData(true, "AfterManifestJournalAppend")]
    [InlineData(false, "BeforeManifestJournalSync")]
    [InlineData(true, "BeforeManifestJournalSync")]
    [InlineData(false, "AfterManifestJournalSync")]
    [InlineData(true, "AfterManifestJournalSync")]
    [InlineData(false, "BeforeManifestCheckpointReplace")]
    [InlineData(true, "BeforeManifestCheckpointReplace")]
    public async Task ShouldFenceLocalDdlAfterJournalWriteFailureAndRecoverCommittedAuthority(bool drop, string phase)
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new ArmableFailpointHandler();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(failpoints)))
        {
            var family = await database.ColumnFamilies.CreateAsync("existing");
            failpoints.Arm(Enum.Parse<Failpoint>(phase));
            await Assert.ThrowsAsync<PantsCommitOutcomeUnknownException>(() => drop
                ? database.ColumnFamilies.DropAsync(family).AsTask()
                : (Task)database.ColumnFamilies.CreateAsync("changed").AsTask());

            await Assert.ThrowsAsync<PantsFencedException>(() => database.ColumnFamilies.CreateAsync("later").AsTask());
            await Assert.ThrowsAsync<PantsFencedException>(() => database.ColumnFamilies.DropAsync(family).AsTask());
            await using var write = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
            write.Put("rejected"u8.ToArray(), "value"u8.ToArray());
            await Assert.ThrowsAsync<PantsFencedException>(() => write.CommitAsync(PantsWriteOptions.BestEffort).AsTask());
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.Equal(!drop, await reopened.ColumnFamilies.GetAsync("existing") is not null);
        Assert.Equal(!drop, await reopened.ColumnFamilies.GetAsync("changed") is not null);
        Assert.Null(await reopened.ColumnFamilies.GetAsync("later"));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync("rejected"u8.ToArray()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldKeepPreWriteDdlFailureDefiniteAndAllowRetry(bool drop)
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new ArmableFailpointHandler();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(failpoints)))
        {
            var family = await database.ColumnFamilies.CreateAsync("existing");
            failpoints.Arm(Failpoint.BeforeManifestJournalAppend);
            await Assert.ThrowsAsync<PantsIOException>(() => drop
                ? database.ColumnFamilies.DropAsync(family).AsTask()
                : (Task)database.ColumnFamilies.CreateAsync("changed").AsTask());
            if (drop)
            {
                await database.ColumnFamilies.DropAsync(family);
            }
            else
            {
                await database.ColumnFamilies.CreateAsync("changed");
            }
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.Equal(!drop, await reopened.ColumnFamilies.GetAsync("existing") is not null);
        Assert.Equal(!drop, await reopened.ColumnFamilies.GetAsync("changed") is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRecoverCloudDdlAfterUncertainLocalJournalWithoutDuplicatingTheEdit(bool drop)
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new ArmableFailpointHandler();
        var options = PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "ddl-journal/")
            .WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(failpoints)))
        {
            await using (var seed = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite))
            {
                seed.Put("anchor"u8.ToArray(), "durable"u8.ToArray());
                await seed.CommitAsync(PantsWriteOptions.CloudStrict);
            }

            var family = await database.ColumnFamilies.CreateAsync("existing");
            failpoints.Arm(Failpoint.AfterManifestJournalSync);
            await Assert.ThrowsAsync<PantsCommitOutcomeUnknownException>(() => drop
                ? database.ColumnFamilies.DropAsync(family).AsTask()
                : (Task)database.ColumnFamilies.CreateAsync("changed").AsTask());
            await Assert.ThrowsAsync<PantsFencedException>(() => database.ColumnFamilies.CreateAsync("later").AsTask());
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.Equal(!drop, await reopened.ColumnFamilies.GetAsync("existing") is not null);
        Assert.Equal(!drop, await reopened.ColumnFamilies.GetAsync("changed") is not null);
        Assert.Null(await reopened.ColumnFamilies.GetAsync("later"));
        Assert.False(File.Exists(Path.Combine(directory.Path, "ddl.prepare.json")));
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        Assert.Equal("durable"u8.ToArray(), (await reader.GetAsync("anchor"u8.ToArray()))?.ToArray());
    }

    sealed class JournalBarrier : IFailpointHandler, IDisposable
    {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly ManualResetEventSlim _release = new();
        int _armed;

        public Task Entered => _entered.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _release.Set();

        public void Hit(Failpoint failpoint)
        {
            if (failpoint == Failpoint.BeforeManifestJournalSync && Interlocked.Exchange(ref _armed, 0) != 0)
            {
                _entered.TrySetResult();
                if (!_release.Wait(TestTimeouts.Expected))
                {
                    throw new TimeoutException("DDL journal barrier was not released.");
                }
            }
        }

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
        }
    }
}
