namespace Pants.Tests;

public sealed class PantsFailureInjectionTests
{
    [Fact]
    public async Task ShouldFailBeforeWalAppendWithoutPublishingTransaction()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new TestFailpointHandler(PantsFailpoint.BeforeWalAppend);
        await using IPantsDatabase database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(failpoints));
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());

        PantsIOException error = await Assert.ThrowsAsync<PantsIOException>(() =>
            transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());

        Assert.Contains(nameof(PantsFailpoint.BeforeWalAppend), error.Message, StringComparison.Ordinal);
        await using IPantsTransaction reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync("key"u8.ToArray()));
        Assert.Equal(1, failpoints.HitCount);
    }

    [Fact]
    public async Task ShouldRetryIdenticalFlushAfterOutputBecomesDurableBeforePublication()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new TestFailpointHandler(
            PantsFailpoint.AfterFlushOutputDurable,
            oneShot: true);
        await using IPantsDatabase database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(failpoints));
        await using (IPantsTransaction transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        await Assert.ThrowsAsync<PantsIOException>(() =>
            database.FlushAsync(database.DefaultColumnFamily).AsTask());
        Assert.Equal(1, (await database.GetRuntimeMetricsAsync()).ObsoleteFileBacklog);

        await database.FlushAsync(database.DefaultColumnFamily);

        Assert.Equal(0, (await database.GetRuntimeMetricsAsync()).ObsoleteFileBacklog);
        Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
    }

    [Fact]
    public async Task ShouldRecoverFullyAbsentTransactionGivenPartialWalFrame()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new TestFailpointHandler(PantsFailpoint.MidWalAppend);
        await using (IPantsDatabase database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path),
                         new PantsRuntimeDependencies(failpoints)))
        {
            await using IPantsTransaction transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("first"u8.ToArray(), "one"u8.ToArray());
            transaction.Put("second"u8.ToArray(), "two"u8.ToArray());

            await Assert.ThrowsAsync<PantsIOException>(
                () => transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());
        }

        await using IPantsDatabase reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using IPantsTransaction reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync("first"u8.ToArray()));
        Assert.Null(await reader.GetAsync("second"u8.ToArray()));
    }

    private sealed class TestFailpointHandler(
        PantsFailpoint target,
        bool oneShot = false) : IPantsFailpointHandler
    {
        private int _hitCount;

        public int HitCount => Volatile.Read(ref _hitCount);

        public void Hit(PantsFailpoint failpoint)
        {
            if (failpoint == target && (!oneShot || HitCount == 0))
            {
                Interlocked.Increment(ref _hitCount);
                throw new IOException($"Injected failure at {failpoint}.");
            }
        }
    }
}
