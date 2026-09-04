using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Contracts;

public sealed class PantsDurabilityParityTests
{
    [Fact]
    public async Task ShouldRecoverEveryConcurrentSyncCommitExactlyOnce()
    {
        using var directory = new TemporaryDirectory();
        const int commitCount = 48;
        await using (var database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path).WithWalBufferSize(128)))
        {
            var commits = Enumerable.Range(0, commitCount)
                .Select(index => CommitAsync(database, index))
                .ToArray();
            await Task.WhenAll(commits).WaitAsync(TimeSpan.FromSeconds(20));

            var liveMetrics = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.True(liveMetrics.DurabilityWaitersFannedOutTotal > 0);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory.Path, "wal"), "*.wal"));
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        for (var index = 0; index < commitCount; index++)
        {
            Assert.Equal($"value-{index:00}", await ReadAsync(reopened, $"key-{index:00}"));
        }

        var metrics = await reopened.Diagnostics.GetRuntimeMetricsAsync();
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
            await using var database = await PantsDatabase.OpenAsync(
                PantsOpenOptions.Local(directory.Path));
            Assert.True((await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence >= previousSequence);
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("value"u8.ToArray(), TestBytes.FromString($"cycle-{cycle}"));
            if (cycle > 0)
            {
                transaction.Delete(TestBytes.FromString($"delete-{cycle - 1}"));
            }

            transaction.Put(TestBytes.FromString($"delete-{cycle}"), "present"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Sync);
            previousSequence = (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence;
        }

        await using var reopened = await PantsDatabase.OpenAsync(
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
        var options = PantsOpenOptions.Local(directory.Path)
            .WithWalBufferSize(128);
        await using (var database = await PantsDatabase.OpenAsync(options))
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

        await using var reopened = await PantsDatabase.OpenAsync(options);
        for (var index = 0; index < 3; index++)
        {
            Assert.Equal(
                new string((char)('a' + index), 256),
                await ReadAsync(reopened, $"key-{index}"));
        }

        Assert.True((await reopened.Diagnostics.GetRuntimeMetricsAsync()).WalRecoveryRecordsReplayed >= 3);
    }

    [Fact]
    public async Task ShouldRotateMixedEpochActiveWalGivenLocalReopen()
    {
        using var directory = new TemporaryDirectory();
        await using (var database = await PantsDatabase.OpenAsync(
                         PantsOpenOptions.Local(directory.Path)))
        {
            await CommitValueAsync(database, "first", "first-value");
        }

        var rotatingOptions = PantsOpenOptions.Local(directory.Path)
            .WithWalBufferSize(128);
        await using (var database = await PantsDatabase.OpenAsync(rotatingOptions))
        {
            await CommitValueAsync(database, "second", new string('s', 256));
        }

        await using var reopened = await PantsDatabase.OpenAsync(rotatingOptions);

        Assert.Equal("first-value", await ReadAsync(reopened, "first"));
        Assert.Equal(new string('s', 256), await ReadAsync(reopened, "second"));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory.Path, "wal"), "*.wal"));
    }

    [Theory]
    [InlineData(nameof(Failpoint.BeforeWalAppend))]
    [InlineData(nameof(Failpoint.MidWalAppend))]
    [InlineData(nameof(Failpoint.AfterWalAppend))]
    [InlineData(nameof(Failpoint.BeforeWalFlush))]
    public async Task ShouldRecoverAnAtomicOutcomeAtEveryWalBoundary(string failpointName)
    {
        using var directory = new TemporaryDirectory();
        var failpoint = Enum.Parse<Failpoint>(failpointName);
        var handler = new OneShotFailpointHandler(failpoint);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path),
                         new RuntimeDependencies(handler)))
        {
            await using var transaction = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadWrite);
            transaction.Put("first"u8.ToArray(), "one"u8.ToArray());
            transaction.Put("second"u8.ToArray(), "two"u8.ToArray());
            await Assert.ThrowsAnyAsync<PantsException>(() => transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        var first = await ReadAsync(reopened, "first");
        var second = await ReadAsync(reopened, "second");
        Assert.Equal(first is null, second is null);
        if (first is not null)
        {
            Assert.Equal("one", first);
            Assert.Equal("two", second);
        }
    }

    [Theory]
    [InlineData(nameof(Failpoint.BeforeWalAppend))]
    [InlineData(nameof(Failpoint.MidWalAppend))]
    [InlineData(nameof(Failpoint.AfterWalAppend))]
    [InlineData(nameof(Failpoint.BeforeWalFlush))]
    public async Task ShouldNotRecoverRejectedCommitGivenLaterSyncSucceeds(string failpointName)
    {
        using var directory = new TemporaryDirectory();
        var failpoint = Enum.Parse<Failpoint>(failpointName);
        var handler = new OneShotFailpointHandler(failpoint);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path),
                         new RuntimeDependencies(handler)))
        {
            await using (var rejected = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                rejected.Put("rejected"u8.ToArray(), "ghost"u8.ToArray());
                await Assert.ThrowsAnyAsync<PantsException>(() =>
                    rejected.CommitAsync(PantsWriteOptions.Sync).AsTask());
            }

            await CommitValueAsync(database, "accepted", "durable");
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        Assert.Null(await ReadAsync(reopened, "rejected"));
        Assert.Equal("durable", await ReadAsync(reopened, "accepted"));
    }

    [Fact]
    public async Task ShouldAcknowledgeSyncGivenAfterWalFlushHookFails()
    {
        using var directory = new TemporaryDirectory();
        var handler = new OneShotFailpointHandler(Failpoint.AfterWalFlush);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path),
                         new RuntimeDependencies(handler)))
        {
            await CommitValueAsync(database, "durable", "value");

            Assert.Equal("value", await ReadAsync(database, "durable"));
            Assert.Equal(
                PantsEngineHealth.Degraded,
                (await database.Diagnostics.GetRuntimeMetricsAsync()).Health);
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        Assert.Equal("value", await ReadAsync(reopened, "durable"));
    }

    [Fact]
    public async Task ShouldAcknowledgeSyncGivenWalRotationFailsAfterDurabilityBoundary()
    {
        using var directory = new TemporaryDirectory();
        var handler = new OneShotFailpointHandler(Failpoint.BeforeWalRotation);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path)
                             .WithBackgroundCompaction(false)
                             .WithWalBufferSize(1),
                         new RuntimeDependencies(handler)))
        {
            await CommitValueAsync(database, "rotation-authoritative", "first");

            Assert.Equal("first", await ReadAsync(database, "rotation-authoritative"));
            Assert.Equal(
                PantsEngineHealth.Degraded,
                (await database.Diagnostics.GetRuntimeMetricsAsync()).Health);
            await CommitValueAsync(database, "rotation-followup", "second");
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        Assert.Equal("first", await ReadAsync(reopened, "rotation-authoritative"));
        Assert.Equal("second", await ReadAsync(reopened, "rotation-followup"));
    }

    [Fact]
    public async Task ShouldAcknowledgeSyncGivenWalRecordThresholdFlushFailsAfterDurabilityBoundary()
    {
        using var directory = new TemporaryDirectory();
        var handler = new OneShotFailpointHandler(Failpoint.BeforeWalRecordThresholdFlush);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path)
                             .WithBackgroundCompaction(false)
                             .WithFlushAfterWalRecordsForTesting(1),
                         new RuntimeDependencies(handler)))
        {
            await CommitValueAsync(database, "threshold-authoritative", "value");

            Assert.Equal("value", await ReadAsync(database, "threshold-authoritative"));
            Assert.Equal(
                PantsEngineHealth.Degraded,
                (await database.Diagnostics.GetRuntimeMetricsAsync()).Health);
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        Assert.Equal("value", await ReadAsync(reopened, "threshold-authoritative"));
    }

    [Theory]
    [InlineData(nameof(Failpoint.AfterFlushOutputDurable))]
    [InlineData(nameof(Failpoint.BeforeFlushManifestPublish))]
    [InlineData(nameof(Failpoint.AfterFlushManifestPublish))]
    public async Task ShouldRecoverReadableDataAtEveryFlushPublicationBoundary(string failpointName)
    {
        using var directory = new TemporaryDirectory();
        var failpoint = Enum.Parse<Failpoint>(failpointName);
        var handler = new OneShotFailpointHandler(failpoint);
        await using (var database = await PantsDatabase.OpenForTestingAsync(
                         PantsOpenOptions.Local(directory.Path),
                         new RuntimeDependencies(handler)))
        {
            await CommitValueAsync(database, "key", "value");
            await Assert.ThrowsAnyAsync<PantsException>(() =>
                database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask());
        }

        await using var reopened = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path));
        Assert.Equal("value", await ReadAsync(reopened, "key"));
    }

    [Fact]
    public async Task ShouldRemainReadableAfterFlushCompactionShutdownAndReopenCycles()
    {
        using var directory = new TemporaryDirectory();
        for (var cycle = 0; cycle < 4; cycle++)
        {
            await using var database = await PantsDatabase.OpenAsync(
                PantsOpenOptions.Local(directory.Path));
            await CommitValueAsync(database, $"key-{cycle}", $"value-{cycle}");
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
            await database.Maintenance.CompactAllAsync();
            await database.ShutdownAsync(TimeSpan.FromSeconds(10));
        }

        await using var reopened = await PantsDatabase.OpenAsync(
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
            Failpoint.BeforeWalAppend,
            static failpoint => new PantsNoSpaceException($"No space at {failpoint}."));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path),
            new RuntimeDependencies(handler));
        await using (var rejected = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadWrite))
        {
            rejected.Put("rejected"u8.ToArray(), "value"u8.ToArray());
            var error = await Assert.ThrowsAsync<PantsNoSpaceException>(() =>
                rejected.CommitAsync(PantsWriteOptions.Sync).AsTask());
            Assert.Equal(PantsErrorCode.NoSpace, error.Code);
        }

        Assert.Null(await ReadAsync(database, "rejected"));
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(1, metrics.NoSpaceEvents);
        Assert.Equal(1, metrics.WriteStallsTotal);
        Assert.Equal(0, metrics.WriteStallsMemoryTotal);
        Assert.Equal(0, metrics.WriteStallsCompactionTotal);
        Assert.Equal(0, metrics.WriteStallsCloudTotal);
        Assert.Equal(1, metrics.WriteStallsNoSpaceTotal);
        await CommitValueAsync(database, "accepted", "value");
        Assert.Equal("value", await ReadAsync(database, "accepted"));
    }

    static async Task CommitAsync(IPantsDatabase database, int index) =>
        await CommitValueAsync(database, $"key-{index:00}", $"value-{index:00}");

    static async Task CommitValueAsync(
        IPantsDatabase database,
        string key,
        string value)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static async Task<string?> ReadAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(TestBytes.FromString(key));
        return value is null ? null : TestBytes.ToText(value.Value);
    }

    sealed class OneShotFailpointHandler(
        Failpoint target,
        Func<Failpoint, Exception>? exceptionFactory = null) : IFailpointHandler
    {
        int _triggered;

        public void Hit(Failpoint failpoint)
        {
            if (failpoint == target && Interlocked.Exchange(ref _triggered, 1) == 0)
            {
                throw exceptionFactory?.Invoke(failpoint) ??
                      new IOException($"Injected failure at {failpoint}.");
            }
        }
    }
}
