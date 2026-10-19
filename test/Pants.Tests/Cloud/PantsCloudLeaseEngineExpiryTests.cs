using System.Globalization;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Cloud;

public sealed class PantsCloudLeaseEngineExpiryTests
{
    [Fact]
    public async Task ShouldReportFencingBeforeCloudUploadStallGivenLeaseExpires()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        using var uploadFailpoint = new FlushPipelineFailpointHandler(Failpoint.AfterCloudWalUpload);
        var monotonic = new ManualSchedulingTimeProvider();
        var wallClock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = PantsOpenOptions.Cloud(cache.Path, CreateLocation())
            .WithBackgroundCompaction(false)
            .WithCloudWritePolicy(new PantsCloudWritePolicy(
                long.MaxValue,
                long.MaxValue,
                TimeSpan.FromHours(1),
                1))
            .WithCoordinatorQueueCapacityForTesting(1)
            .WithLeaseClockSkewTolerance(TimeSpan.FromMilliseconds(100))
            .WithLeaseTimeToLive(TimeSpan.FromSeconds(2));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(
                failpoints: uploadFailpoint,
                cloudHttpClient: client,
                leaseHeartbeatInterval: TimeSpan.FromMilliseconds(100),
                runtimeTimeProvider: monotonic,
                leaseClock: wallClock));
        await CommitAsync(database, "occupy-upload", PantsWriteOptions.CloudAsync);
        await uploadFailpoint.WaitUntilEnteredAsync(TestTimeouts.Expected);
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.True(before.PendingCloudUploads > 0);

        await using var contender = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        contender.Put("after-expiry"u8.ToArray(), "value"u8.ToArray());

        try
        {
            handler.BlockLeaseWrites();
            monotonic.Advance(TimeSpan.FromMilliseconds(100));
            await handler.WaitForBlockedLeaseWriteAsync();
            monotonic.Advance(TimeSpan.FromSeconds(2) + TimeSpan.FromMilliseconds(1));
            Assert.False(database.PersistentStorage!.IsPrimaryLeaseHealthy);

            var failure = await Assert.ThrowsAsync<PantsFencedException>(() =>
                contender.CommitAsync(PantsWriteOptions.CloudAsync).AsTask());

            Assert.Equal(PantsErrorCode.Fenced, failure.Code);
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(before.WalAppendCount, after.WalAppendCount);
        }
        finally
        {
            uploadFailpoint.Release();
            handler.ReleaseLeaseWrites();
        }
    }

    [Fact]
    public async Task ShouldFenceEveryCoalescedCloudWriteWhenLeaseExpiresDuringPreparation()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        using var failpoints = new CoalescedCommitPreparationFailpointHandler();
        var monotonic = new ManualSchedulingTimeProvider();
        var wallClock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = PantsOpenOptions.Cloud(cache.Path, CreateLocation())
            .WithBackgroundCompaction(false)
            .WithLeaseClockSkewTolerance(TimeSpan.FromMilliseconds(100))
            .WithLeaseTimeToLive(TimeSpan.FromSeconds(2));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(
                failpoints: failpoints,
                cloudHttpClient: client,
                leaseHeartbeatInterval: TimeSpan.FromMilliseconds(100),
                runtimeTimeProvider: monotonic,
                leaseClock: wallClock));
        await using var first = await BeginWriteAsync(database, "coalesced-first");
        await using var second = await BeginWriteAsync(database, "coalesced-second");

        var blockedMetrics = database.Diagnostics.GetRuntimeMetricsAsync().AsTask();
        await failpoints.WaitForRuntimeBarrierAsync(TestTimeouts.Expected);
        var firstCommit = first.CommitAsync(PantsWriteOptions.CloudAsync).AsTask();
        var secondCommit = second.CommitAsync(PantsWriteOptions.CloudAsync).AsTask();
        failpoints.ReleaseRuntimeBarrier();
        var before = await blockedMetrics.WaitAsync(TestTimeouts.Expected);
        await failpoints.WaitForCommitPreparationAsync(TestTimeouts.Expected);

        try
        {
            handler.BlockLeaseWrites();
            monotonic.Advance(TimeSpan.FromMilliseconds(100));
            await handler.WaitForBlockedLeaseWriteAsync();
            monotonic.Advance(TimeSpan.FromSeconds(2) + TimeSpan.FromMilliseconds(1));
            Assert.False(database.PersistentStorage!.IsPrimaryLeaseHealthy);

            failpoints.ReleaseCommitPreparation();
            var firstFailure = await Assert.ThrowsAsync<PantsFencedException>(() =>
                firstCommit.WaitAsync(TestTimeouts.Expected));
            var secondFailure = await Assert.ThrowsAsync<PantsFencedException>(() =>
                secondCommit.WaitAsync(TestTimeouts.Expected));

            Assert.Equal(PantsErrorCode.Fenced, firstFailure.Code);
            Assert.Equal(PantsErrorCode.Fenced, secondFailure.Code);
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(before.WalAppendCount, after.WalAppendCount);
            await using var reader = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadOnly);
            Assert.Null(await reader.GetAsync("coalesced-first"u8.ToArray()));
            Assert.Null(await reader.GetAsync("coalesced-second"u8.ToArray()));
        }
        finally
        {
            failpoints.ReleaseCommitPreparation();
            handler.ReleaseLeaseWrites();
        }
    }

    [Fact]
    public async Task ShouldRejectCloudWalAppendAfterRemoteLeaseTakeoverAndReopenCleanly()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var monotonic = new ManualSchedulingTimeProvider();
        var wallClock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = PantsOpenOptions.Cloud(cache.Path, CreateLocation())
            .WithBackgroundCompaction(false)
            .WithLeaseClockSkewTolerance(TimeSpan.FromMilliseconds(100))
            .WithLeaseTimeToLive(TimeSpan.FromSeconds(2));
        var dependencies = new RuntimeDependencies(
            cloudHttpClient: client,
            leaseHeartbeatInterval: TimeSpan.FromMilliseconds(100),
            runtimeTimeProvider: monotonic,
            leaseClock: wallClock);
        await using var database = await PantsDatabase.OpenForTestingAsync(options, dependencies);
        await using var transaction = await BeginWriteAsync(database, "remote-takeover");
        var before = await database.Diagnostics.GetRuntimeMetricsAsync();

        Assert.True(database.PersistentStorage!.IsPrimaryLeaseHealthy);
        var currentLease = handler.GetObjectText("midge_primary_lease.json");
        handler.ReplaceObjectText(
            "midge_primary_lease.json",
            CreateSuccessorLeaseDocument(currentLease));
        Assert.True(database.PersistentStorage.IsPrimaryLeaseHealthy);

        var failure = await Assert.ThrowsAsync<PantsFencedException>(() =>
            transaction.CommitAsync(PantsWriteOptions.CloudAsync).AsTask());

        Assert.Equal(PantsErrorCode.Fenced, failure.Code);
        Assert.False(database.PersistentStorage.IsPrimaryLeaseHealthy);
        var after = await database.Diagnostics.GetRuntimeMetricsAsync();
        Assert.Equal(before.WalAppendCount, after.WalAppendCount);
        await using (var reader = await database.Transactions.BeginAsync(
                         database.ColumnFamilies.DefaultFamily,
                         PantsTransactionMode.ReadOnly))
        {
            Assert.Null(await reader.GetAsync("remote-takeover"u8.ToArray()));
        }

        await database.DisposeAsync();
        var leaseLines = handler.GetObjectText("midge_primary_lease.json").Split('\n');
        var expiredLease = string.Join(
            '\n',
            leaseLines.Select(static line => line.StartsWith("expires_at: ", StringComparison.Ordinal)
                ? $"expires_at: {DateTimeOffset.UnixEpoch:O}"
                : line));
        handler.ReplaceObjectText("midge_primary_lease.json", expiredLease);

        await using var reopened = await PantsDatabase.OpenForTestingAsync(options, dependencies);
        await using var reopenedReader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reopenedReader.GetAsync("remote-takeover"u8.ToArray()));
    }

    [Fact]
    public async Task ShouldRejectCoalescedCloudWalAppendAfterRemoteLeaseTakeover()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        using var failpoints = new CoalescedCommitPreparationFailpointHandler();
        var monotonic = new ManualSchedulingTimeProvider();
        var wallClock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = PantsOpenOptions.Cloud(cache.Path, CreateLocation())
            .WithBackgroundCompaction(false)
            .WithLeaseClockSkewTolerance(TimeSpan.FromMilliseconds(100))
            .WithLeaseTimeToLive(TimeSpan.FromSeconds(2));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(
                failpoints: failpoints,
                cloudHttpClient: client,
                leaseHeartbeatInterval: TimeSpan.FromMilliseconds(100),
                runtimeTimeProvider: monotonic,
                leaseClock: wallClock));
        await using var first = await BeginWriteAsync(database, "takeover-first");
        await using var second = await BeginWriteAsync(database, "takeover-second");

        var blockedMetrics = database.Diagnostics.GetRuntimeMetricsAsync().AsTask();
        await failpoints.WaitForRuntimeBarrierAsync(TestTimeouts.Expected);
        var firstCommit = first.CommitAsync(PantsWriteOptions.CloudAsync).AsTask();
        var secondCommit = second.CommitAsync(PantsWriteOptions.CloudAsync).AsTask();
        failpoints.ReleaseRuntimeBarrier();
        var before = await blockedMetrics.WaitAsync(TestTimeouts.Expected);
        await failpoints.WaitForCommitPreparationAsync(TestTimeouts.Expected);

        try
        {
            Assert.True(database.PersistentStorage!.IsPrimaryLeaseHealthy);
            var currentLease = handler.GetObjectText("midge_primary_lease.json");
            handler.ReplaceObjectText(
                "midge_primary_lease.json",
                CreateSuccessorLeaseDocument(currentLease));
            Assert.True(database.PersistentStorage.IsPrimaryLeaseHealthy);

            failpoints.ReleaseCommitPreparation();
            var firstFailure = await Assert.ThrowsAsync<PantsFencedException>(() =>
                firstCommit.WaitAsync(TestTimeouts.Expected));
            var secondFailure = await Assert.ThrowsAsync<PantsFencedException>(() =>
                secondCommit.WaitAsync(TestTimeouts.Expected));

            Assert.Equal(PantsErrorCode.Fenced, firstFailure.Code);
            Assert.Equal(PantsErrorCode.Fenced, secondFailure.Code);
            var after = await database.Diagnostics.GetRuntimeMetricsAsync();
            Assert.Equal(before.WalAppendCount, after.WalAppendCount);
            await using var reader = await database.Transactions.BeginAsync(
                database.ColumnFamilies.DefaultFamily,
                PantsTransactionMode.ReadOnly);
            Assert.Null(await reader.GetAsync("takeover-first"u8.ToArray()));
            Assert.Null(await reader.GetAsync("takeover-second"u8.ToArray()));
        }
        finally
        {
            failpoints.ReleaseCommitPreparation();
        }
    }

    [Fact]
    public async Task ShouldFenceEveryWritePathAndFireLossOnceWhenRenewalBlocksPastTheDeadline()
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var monotonic = new ManualSchedulingTimeProvider();
        var wallClock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var losses = 0;
        var options = PantsOpenOptions.Cloud(cache.Path, CreateLocation())
            .WithBackgroundCompaction(false)
            .WithLeaseClockSkewTolerance(TimeSpan.FromMilliseconds(100))
            .WithLeaseTimeToLive(TimeSpan.FromSeconds(2))
            .WithLeaseLossCallback(() => Interlocked.Increment(ref losses));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(
                cloudHttpClient: client,
                leaseHeartbeatInterval: TimeSpan.FromMilliseconds(100),
                runtimeTimeProvider: monotonic,
                leaseClock: wallClock));
        await CommitAsync(database, "before", PantsWriteOptions.CloudStrict);
        Assert.True(database.PersistentStorage!.IsPrimaryLeaseHealthy);

        // Renewal now hangs: the next heartbeat tick reaches the blocked lease write.
        handler.BlockLeaseWrites();
        monotonic.Advance(TimeSpan.FromMilliseconds(100));
        await handler.WaitForBlockedLeaseWriteAsync();

        // The hung renewal never lands, so the monotonic deadline passes with no caller involved.
        monotonic.Advance(TimeSpan.FromSeconds(2) + TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, Volatile.Read(ref losses));
        Assert.False(database.PersistentStorage.IsPrimaryLeaseHealthy);
        await Assert.ThrowsAsync<PantsFencedException>(() =>
            CommitAsync(database, "strict", PantsWriteOptions.CloudStrict));
        await Assert.ThrowsAsync<PantsFencedException>(() =>
            CommitAsync(database, "async", PantsWriteOptions.CloudAsync));
        await Assert.ThrowsAsync<PantsFencedException>(() =>
            database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask());
        await Assert.ThrowsAsync<PantsFencedException>(() =>
            database.Maintenance.CompactAllAsync().AsTask());

        // The hung renewal finally completes; authority must not come back.
        handler.ReleaseLeaseWrites();
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(database.PersistentStorage.IsPrimaryLeaseHealthy);
        Assert.Equal(1, Volatile.Read(ref losses));
        await Assert.ThrowsAsync<PantsFencedException>(() =>
            CommitAsync(database, "after-release", PantsWriteOptions.CloudStrict));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldFenceEveryCloudCommitAndMaintenanceGivenRenewalBlockedAndWallClockStalled(
        bool wallClockStepsBack)
    {
        using var cache = new TemporaryDirectory();
        using var handler = new InMemoryAzureBlobHandler();
        using var client = new HttpClient(handler);
        var monotonic = new ManualSchedulingTimeProvider();
        var wallClock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var losses = 0;
        var options = PantsOpenOptions.Cloud(cache.Path, CreateLocation())
            .WithBackgroundCompaction(false)
            .WithLeaseClockSkewTolerance(TimeSpan.FromMilliseconds(100))
            .WithLeaseTimeToLive(TimeSpan.FromSeconds(2))
            .WithLeaseLossCallback(() => Interlocked.Increment(ref losses));
        await using var database = await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(
                cloudHttpClient: client,
                leaseHeartbeatInterval: TimeSpan.FromMilliseconds(100),
                runtimeTimeProvider: monotonic,
                leaseClock: wallClock));
        await CommitAsync(database, "before", PantsWriteOptions.CloudStrict);
        Assert.True(database.PersistentStorage!.IsPrimaryLeaseHealthy);

        // Renewal hangs, the wall clock stalls or steps backward, and the monotonic deadline passes
        // while no caller touches the database. The watchdog timer alone must report the loss.
        handler.BlockLeaseWrites();
        if (wallClockStepsBack)
        {
            wallClock.UtcNow -= TimeSpan.FromHours(1);
        }

        monotonic.Advance(TimeSpan.FromMilliseconds(100));
        await handler.WaitForBlockedLeaseWriteAsync();
        monotonic.Advance(TimeSpan.FromSeconds(2) + TimeSpan.FromMilliseconds(1));

        Assert.Equal(1, Volatile.Read(ref losses));
        Assert.False(database.PersistentStorage.IsPrimaryLeaseHealthy);
        await Assert.ThrowsAsync<PantsFencedException>(() =>
            CommitAsync(database, "async", PantsWriteOptions.CloudAsync));
        await Assert.ThrowsAsync<PantsFencedException>(() =>
            CommitAsync(database, "strict", PantsWriteOptions.CloudStrict));
        await Assert.ThrowsAsync<PantsFencedException>(() =>
            database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily).AsTask());
        await Assert.ThrowsAsync<PantsFencedException>(() =>
            database.Maintenance.CompactAllAsync().AsTask());

        // Releasing the hung renewal must not restore authority or notify loss a second time.
        handler.ReleaseLeaseWrites();
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.False(database.PersistentStorage.IsPrimaryLeaseHealthy);
        Assert.Equal(1, Volatile.Read(ref losses));
        await Assert.ThrowsAsync<PantsFencedException>(() =>
            CommitAsync(database, "after-release", PantsWriteOptions.CloudStrict));
    }

    static async Task CommitAsync(IPantsDatabase database, string key, PantsWriteOptions writeOptions)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString("value"));
        await transaction.CommitAsync(writeOptions);
    }

    static async ValueTask<IPantsTransaction> BeginWriteAsync(IPantsDatabase database, string key)
    {
        var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString("value"));
        return transaction;
    }

    static string CreateSuccessorLeaseDocument(string currentLease)
    {
        var lines = currentLease.Split('\n');
        var epochLine = lines.Single(static line => line.StartsWith("epoch: ", StringComparison.Ordinal));
        var currentEpoch = ulong.Parse(epochLine.AsSpan("epoch: ".Length), CultureInfo.InvariantCulture);
        var successorEpoch = checked(currentEpoch + 1);
        return string.Join(
            '\n',
            lines.Select(line =>
            {
                if (line.StartsWith("epoch: ", StringComparison.Ordinal))
                {
                    return $"epoch: {successorEpoch}";
                }

                if (line.StartsWith("holder_id: ", StringComparison.Ordinal))
                {
                    return "holder_id: successor";
                }

                return line.StartsWith("owner_token: ", StringComparison.Ordinal)
                    ? "owner_token: successor-token"
                    : line;
            }));
    }

    static PantsCloudStorageLocation CreateLocation() => new(
        new PantsAzureBlobProvider(
            "account",
            "container",
            new Uri("https://storage.example.test"),
            new PantsAzureCredentialSource.SasToken("sig=test")),
        "database");
}
