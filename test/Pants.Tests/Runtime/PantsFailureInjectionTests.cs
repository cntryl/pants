namespace Cntryl.Pants.Tests.Runtime;

public sealed class PantsFailureInjectionTests
{
    [Fact]
    public async Task ShouldFailBeforeWalAppendWithoutPublishingTransaction()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new TestFailpointHandler(PantsFailpoint.BeforeWalAppend);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(failpoints));
        await using var transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray());

        var error = await Assert.ThrowsAsync<PantsIOException>(() =>
            transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());

        Assert.Contains(nameof(PantsFailpoint.BeforeWalAppend), error.Message, StringComparison.Ordinal);
        await using var reader = await database.BeginTransactionAsync(
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
            true);
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(failpoints));
        await using (var transaction = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            transaction.Put("key"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        await Assert.ThrowsAsync<PantsIOException>(() =>
            database.FlushAsync(database.DefaultColumnFamily).AsTask());
        Assert.Equal(0, (await database.GetRuntimeMetricsAsync()).ObsoleteFileBacklog);
        Assert.Empty(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        Assert.Single(Directory.GetFiles(
            Path.Combine(directory.Path, "sst", ".flush-staging"),
            "*.tmp"));

        await database.FlushAsync(database.DefaultColumnFamily);

        Assert.Equal(0, (await database.GetRuntimeMetricsAsync()).ObsoleteFileBacklog);
        Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "sst"), "*.sst"));
        Assert.Empty(Directory.GetFiles(
            Path.Combine(directory.Path, "sst", ".flush-staging"),
            "*.tmp"));
    }

    [Fact]
    public async Task ShouldRecoverFullyAbsentTransactionGivenPartialWalFrame()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new TestFailpointHandler(PantsFailpoint.MidWalAppend);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path),
                         new PantsRuntimeDependencies(failpoints)))
        {
            await using var transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("first"u8.ToArray(), "one"u8.ToArray());
            transaction.Put("second"u8.ToArray(), "two"u8.ToArray());

            await Assert.ThrowsAsync<PantsIOException>(() => transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        await using var reader = await reopened.BeginTransactionAsync(
            reopened.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync("first"u8.ToArray()));
        Assert.Null(await reader.GetAsync("second"u8.ToArray()));
    }

    sealed class TestFailpointHandler(
        PantsFailpoint target,
        bool oneShot = false) : IPantsFailpointHandler
    {
        int _hitCount;

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
