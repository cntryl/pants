using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Runtime;

public sealed class PantsDropPublicationTests
{
    [Fact]
    public async Task ShouldRejectNewSnapshotOrRetainItsFilesWhenFamilyReclamationHasStarted()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new ReclamationBarrier();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using var database = await PantsDatabase.OpenForTestingAsync(options, new RuntimeDependencies(failpoints));
        var family = await SeedFamilyAsync(database);
        failpoints.Arm();
        var drop = database.ColumnFamilies.DropAsync(family).AsTask();
        await failpoints.Entered.WaitAsync(TestTimeouts.Expected);
        IPantsTransaction? snapshot = null;
        try
        {
            try
            {
                snapshot = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly);
            }
            catch (PantsInvalidArgumentException)
            {
                // Publication may already exclude the dropped family. An admitted older cut
                // instead owns its SST dependencies until the transaction closes.
            }
        }
        finally
        {
            failpoints.Release();
        }

        await drop;
        if (snapshot is not null)
        {
            await using var admittedSnapshot = snapshot;
            Assert.Equal("durable"u8.ToArray(), (await snapshot.GetAsync("key"u8.ToArray()))?.ToArray());
        }
    }

    [Fact]
    public async Task ShouldRetainDroppedFamilyFilesForSnapshotAdmittedBeforePublication()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = await SeedFamilyAsync(database);
            var input = Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
            await using var snapshot = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly);
            await database.ColumnFamilies.DropAsync(family);
            await Assert.ThrowsAsync<PantsInvalidArgumentException>(() =>
                database.Transactions.BeginAsync(family, PantsTransactionMode.ReadOnly).AsTask());
            Assert.True(File.Exists(input));
            Assert.Equal("durable"u8.ToArray(), (await snapshot.GetAsync("key"u8.ToArray()))?.ToArray());
            await snapshot.DisposeAsync();
            _ = await database.Diagnostics.GetStorageLayoutAsync();
            Assert.False(File.Exists(input));
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        Assert.Null(await reopened.ColumnFamilies.GetAsync("side"));
    }

    static async Task<IPantsColumnFamily> SeedFamilyAsync(IPantsDatabase database)
    {
        var family = await database.ColumnFamilies.CreateAsync("side");
        await using var seed = await database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite);
        seed.Put("key"u8.ToArray(), "durable"u8.ToArray());
        await seed.CommitAsync(PantsWriteOptions.Sync);
        await database.Maintenance.FlushAsync(family);
        return family;
    }

    sealed class ReclamationBarrier : IFailpointHandler, IDisposable
    {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly ManualResetEventSlim _release = new();
        int _journalAppends;
        int _armed;

        public Task Entered => _entered.Task;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _release.Set();

        public void Hit(Failpoint failpoint)
        {
            if (failpoint == Failpoint.BeforeManifestJournalAppend && Volatile.Read(ref _armed) != 0 &&
                Interlocked.Increment(ref _journalAppends) == 2)
            {
                _entered.TrySetResult();
                if (!_release.Wait(TestTimeouts.Expected))
                {
                    throw new TimeoutException("Family reclamation was not released.");
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
