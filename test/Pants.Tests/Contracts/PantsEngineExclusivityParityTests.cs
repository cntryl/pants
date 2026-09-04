using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Contracts;

public sealed class PantsEngineExclusivityParityTests
{
    [Fact]
    public async Task ShouldFenceConcurrentLocalOpensAndReleaseLeaseAfterShutdown()
    {
        using var directory = new TemporaryDirectory();
        var first = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path));
        Assert.True(first.PersistentStorage!.IsPrimaryLeaseHealthy);

        var held = await Assert.ThrowsAsync<PantsLeaseHeldException>(() =>
            PantsDatabase.OpenAsync(PantsOpenOptions.Local(directory.Path)).AsTask());
        Assert.Equal(PantsErrorCode.LeaseHeld, held.Code);
        Assert.Contains("writer", held.Message, StringComparison.OrdinalIgnoreCase);

        await first.ShutdownAsync(TimeSpan.FromSeconds(5));
        await using var second = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        Assert.True(second.PersistentStorage!.IsPrimaryLeaseHealthy);
    }

    [Fact]
    public async Task ShouldAllowExactlyOneWinnerWhenLocalOpensRace()
    {
        using var directory = new TemporaryDirectory();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8)
            .Select(_ => AttemptOpenAsync(directory.Path, start.Task))
            .ToArray();
        start.SetResult();

        var results = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(10));

        var winner = Assert.Single(results, static result => result.Database is not null);
        Assert.All(
            results.Where(static result => result.Database is null),
            static result => Assert.IsType<PantsLeaseHeldException>(result.Error));
        await winner.Database!.DisposeAsync();
    }

    [Fact]
    public async Task ShouldSurviveRapidOpenShutdownCycles()
    {
        using var directory = new TemporaryDirectory();
        for (var cycle = 0; cycle < 10; cycle++)
        {
            await using var database = await PantsDatabase.OpenAsync(
                PantsOpenOptions.Local(directory.Path));
            Assert.True(database.PersistentStorage!.IsPrimaryLeaseHealthy);
            await database.ShutdownAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ShouldFenceWritesAfterLocalLeaseOwnershipIsLost()
    {
        using var directory = new TemporaryDirectory();
        var leaseLost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = PantsOpenOptions.Local(directory.Path)
            .WithLeaseLossCallback(() => leaseLost.TrySetResult());
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(leaseHeartbeatInterval: TimeSpan.FromSeconds(1)));
        await using (var mutationLock = await AcquireLeaseMutationLockAsync(
                         Path.Combine(directory.Path, ".midge_leader.lock")))
        {
            await leaseLost.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.False(database.PersistentStorage!.IsPrimaryLeaseHealthy);
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("fenced"u8.ToArray(), "value"u8.ToArray());
        var error = await Assert.ThrowsAsync<PantsFencedException>(() =>
            transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());
        Assert.Equal(PantsErrorCode.Fenced, error.Code);
    }

    [Fact]
    public async Task ShouldFailClosedGivenDuplicateLocalLeaseFields()
    {
        using var directory = new TemporaryDirectory();
        var leaseLossCount = 0;
        var options = PantsOpenOptions.Local(directory.Path)
            .WithLeaseLossCallback(() => Interlocked.Increment(ref leaseLossCount));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(leaseHeartbeatInterval: TimeSpan.FromHours(1)));
        var leasePath = Path.Combine(directory.Path, ".midge_leader");
        var original = await File.ReadAllTextAsync(leasePath);
        await File.WriteAllTextAsync(leasePath, original + "epoch: 999\n");

        Assert.False(database.PersistentStorage!.IsPrimaryLeaseHealthy);
        Assert.False(database.PersistentStorage!.IsPrimaryLeaseHealthy);
        Assert.Equal(1, Volatile.Read(ref leaseLossCount));
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("fenced"u8.ToArray(), "value"u8.ToArray());
        var fenced = await Assert.ThrowsAsync<PantsFencedException>(() =>
            transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());
        Assert.Equal(PantsErrorCode.Fenced, fenced.Code);
    }

    static async Task<FileStream> AcquireLeaseMutationLockAsync(string path)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (!deadline.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
            }
        }
    }

    static async Task<OpenResult> AttemptOpenAsync(string path, Task start)
    {
        await start;
        try
        {
            var database = await PantsDatabase.OpenAsync(PantsOpenOptions.Local(path));
            return new OpenResult(database, null);
        }
        catch (Exception exception)
        {
            return new OpenResult(null, exception);
        }
    }

    sealed record OpenResult(IPantsDatabase? Database, Exception? Error);
}
