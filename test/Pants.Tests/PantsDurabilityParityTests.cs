namespace Pants.Tests;

public sealed class PantsDurabilityParityTests
{
    [Fact]
    public async Task ShouldRecoverEveryConcurrentSyncCommitExactlyOnce()
    {
        using var directory = new TemporaryDirectory();
        const int commitCount = 48;
        await using (IPantsDatabase database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path).WithWalBufferSize(128)))
        {
            Task[] commits = Enumerable.Range(0, commitCount)
                .Select(index => CommitAsync(database, index))
                .ToArray();
            await Task.WhenAll(commits).WaitAsync(TimeSpan.FromSeconds(20));

            PantsRuntimeMetrics liveMetrics = await database.GetRuntimeMetricsAsync();
            Assert.True(liveMetrics.DurabilityWaitersFannedOutTotal > 0);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory.Path, "wal"), "*.wal"));
        }

        await using IPantsDatabase reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        for (var index = 0; index < commitCount; index++)
        {
            Assert.Equal($"value-{index:00}", await ReadAsync(reopened, $"key-{index:00}"));
        }

        PantsRuntimeMetrics metrics = await reopened.GetRuntimeMetricsAsync();
        Assert.True(metrics.CurrentSequence >= commitCount * 2);
        Assert.True(metrics.WalRecoveryRecordsReplayed >= commitCount);
    }

    [Fact]
    public async Task ShouldPreserveOrderedWritesDeletesAndSequenceAcrossRepeatedReopens()
    {
        using var directory = new TemporaryDirectory();
        long previousSequence = 0;
        for (var cycle = 0; cycle < 5; cycle++)
        {
            await using IPantsDatabase database = await PantsDatabase.OpenAsync(
                PantsOpenOptions.Local(directory.Path));
            Assert.True((await database.GetRuntimeMetricsAsync()).CurrentSequence >= previousSequence);
            await using IPantsTransaction transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("value"u8.ToArray(), TestBytes.FromString($"cycle-{cycle}"));
            if (cycle > 0)
            {
                transaction.Delete(TestBytes.FromString($"delete-{cycle - 1}"));
            }

            transaction.Put(TestBytes.FromString($"delete-{cycle}"), "present"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
            previousSequence = (await database.GetRuntimeMetricsAsync()).CurrentSequence;
        }

        await using IPantsDatabase reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        Assert.Equal("cycle-4", await ReadAsync(reopened, "value"));
        for (var cycle = 0; cycle < 4; cycle++)
        {
            Assert.Null(await ReadAsync(reopened, $"delete-{cycle}"));
        }

        Assert.Equal("present", await ReadAsync(reopened, "delete-4"));
    }

    [Fact]
    public async Task ShouldRotateAndReplayEveryWalSegmentWhenBufferLimitIsExceeded()
    {
        using var directory = new TemporaryDirectory();
        PantsOpenOptions options = PantsOpenOptions.Local(directory.Path)
            .WithWalBufferSize(128);
        await using (IPantsDatabase database = await PantsDatabase.OpenAsync(options))
        {
            for (var index = 0; index < 3; index++)
            {
                await CommitValueAsync(
                    database,
                    $"key-{index}",
                    new string((char)('a' + index), 256));
            }

            Assert.Equal(
                3,
                Directory.GetFiles(Path.Combine(directory.Path, "wal"), "*.wal").Length);
        }

        await using IPantsDatabase reopened = await PantsDatabase.OpenAsync(options);
        for (var index = 0; index < 3; index++)
        {
            Assert.Equal(
                new string((char)('a' + index), 256),
                await ReadAsync(reopened, $"key-{index}"));
        }

        Assert.True((await reopened.GetRuntimeMetricsAsync()).WalRecoveryRecordsReplayed >= 3);
    }

    [Theory]
    [InlineData(nameof(PantsFailpoint.BeforeWalAppend))]
    [InlineData(nameof(PantsFailpoint.MidWalAppend))]
    [InlineData(nameof(PantsFailpoint.AfterWalAppend))]
    [InlineData(nameof(PantsFailpoint.BeforeWalFlush))]
    [InlineData(nameof(PantsFailpoint.AfterWalFlush))]
    public async Task ShouldRecoverAnAtomicOutcomeAtEveryWalBoundary(string failpointName)
    {
        using var directory = new TemporaryDirectory();
        PantsFailpoint failpoint = Enum.Parse<PantsFailpoint>(failpointName);
        var handler = new OneShotFailpointHandler(failpoint);
        await using (IPantsDatabase database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path),
                         new PantsRuntimeDependencies(handler)))
        {
            await using IPantsTransaction transaction = await database.BeginTransactionAsync(
                database.DefaultColumnFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("first"u8.ToArray(), "one"u8.ToArray());
            transaction.Put("second"u8.ToArray(), "two"u8.ToArray());
            await Assert.ThrowsAnyAsync<PantsException>(
                () => transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());
        }

        await using IPantsDatabase reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        string? first = await ReadAsync(reopened, "first");
        string? second = await ReadAsync(reopened, "second");
        Assert.Equal(first is null, second is null);
        if (first is not null)
        {
            Assert.Equal("one", first);
            Assert.Equal("two", second);
        }
    }

    [Theory]
    [InlineData(nameof(PantsFailpoint.AfterFlushOutputDurable))]
    [InlineData(nameof(PantsFailpoint.BeforeFlushManifestPublish))]
    [InlineData(nameof(PantsFailpoint.AfterFlushManifestPublish))]
    public async Task ShouldRecoverReadableDataAtEveryFlushPublicationBoundary(string failpointName)
    {
        using var directory = new TemporaryDirectory();
        PantsFailpoint failpoint = Enum.Parse<PantsFailpoint>(failpointName);
        var handler = new OneShotFailpointHandler(failpoint);
        await using (IPantsDatabase database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path),
                         new PantsRuntimeDependencies(handler)))
        {
            await CommitValueAsync(database, "key", "value");
            await Assert.ThrowsAnyAsync<PantsException>(
                () => database.FlushAsync(database.DefaultColumnFamily).AsTask());
        }

        await using IPantsDatabase reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        Assert.Equal("value", await ReadAsync(reopened, "key"));
    }

    [Fact]
    public async Task ShouldRemainReadableAfterFlushCompactionShutdownAndReopenCycles()
    {
        using var directory = new TemporaryDirectory();
        for (var cycle = 0; cycle < 4; cycle++)
        {
            await using IPantsDatabase database = await PantsDatabase.OpenAsync(
                PantsOpenOptions.Local(directory.Path));
            await CommitValueAsync(database, $"key-{cycle}", $"value-{cycle}");
            await database.FlushAsync(database.DefaultColumnFamily);
            await database.CompactAllAsync();
            await database.ShutdownAsync(TimeSpan.FromSeconds(10));
        }

        await using IPantsDatabase reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        for (var cycle = 0; cycle < 4; cycle++)
        {
            Assert.Equal($"value-{cycle}", await ReadAsync(reopened, $"key-{cycle}"));
        }
    }

    [Fact]
    public async Task ShouldSurfaceNoSpaceWithoutPublishingAndRemainUsable()
    {
        using var directory = new TemporaryDirectory();
        var handler = new OneShotFailpointHandler(
            PantsFailpoint.BeforeWalAppend,
            static failpoint => new PantsNoSpaceException($"No space at {failpoint}."));
        await using IPantsDatabase database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new PantsRuntimeDependencies(handler));
        await using (IPantsTransaction rejected = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            rejected.Put("rejected"u8.ToArray(), "value"u8.ToArray());
            PantsNoSpaceException error = await Assert.ThrowsAsync<PantsNoSpaceException>(
                () => rejected.CommitAsync(PantsWriteOptions.Sync).AsTask());
            Assert.Equal(PantsErrorCode.NoSpace, error.Code);
        }

        Assert.Null(await ReadAsync(database, "rejected"));
        Assert.Equal(1, (await database.GetRuntimeMetricsAsync()).NoSpaceEvents);
        await CommitValueAsync(database, "accepted", "value");
        Assert.Equal("value", await ReadAsync(database, "accepted"));
    }

    private static async Task CommitAsync(IPantsDatabase database, int index) =>
        await CommitValueAsync(database, $"key-{index:00}", $"value-{index:00}");

    private static async Task CommitValueAsync(
        IPantsDatabase database,
        string key,
        string value)
    {
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    private static async Task<string?> ReadAsync(IPantsDatabase database, string key)
    {
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        ReadOnlyMemory<byte>? value = await transaction.GetAsync(TestBytes.FromString(key));
        return value is null ? null : TestBytes.ToText(value.Value);
    }

    private sealed class OneShotFailpointHandler(
        PantsFailpoint target,
        Func<PantsFailpoint, Exception>? exceptionFactory = null) : IPantsFailpointHandler
    {
        private int _triggered;

        public void Hit(PantsFailpoint failpoint)
        {
            if (failpoint == target && Interlocked.Exchange(ref _triggered, 1) == 0)
            {
                throw exceptionFactory?.Invoke(failpoint) ??
                    new IOException($"Injected failure at {failpoint}.");
            }
        }
    }
}
