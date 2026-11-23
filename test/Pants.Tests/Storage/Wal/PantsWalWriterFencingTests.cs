using Cntryl.Pants.Storage.Internal;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Wal;

/// <summary>
///     Once a WAL append may have written bytes, or an fsync has been attempted and failed, the
///     writer cannot know what reached the disk: after a failed fsync the kernel may drop the dirty
///     pages and report the next fsync as successful. Continuing to append could acknowledge a later
///     commit sitting behind a hole, so such failures fence the writer until restart recovery.
/// </summary>
public sealed class PantsWalWriterFencingTests
{
    [Fact]
    public async Task ShouldFenceStartupResidueDeletionAfterLocalLeaseTakeover()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new FlushPipelineFailpointHandler(Failpoint.AfterStartupStagingAuthorityValidation);
        var stagingDirectory = Path.Combine(directory.Path, "sst", ".flush-staging");
        var retainedMarker = Path.Combine(stagingDirectory, "retain-on-takeover.tmp");
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithLeaseTimeToLive(TimeSpan.FromHours(2))
            .WithLeaseClockSkewTolerance(TimeSpan.Zero);
        await using (var seed = await PantsDatabase.OpenForTestingAsync(
                         options,
                         new RuntimeDependencies(new ArmableFailpointHandler())))
        {
        }

        Directory.CreateDirectory(stagingDirectory);
        await File.WriteAllTextAsync(retainedMarker, "keep");
        var open = Task.Run(async () => await PantsDatabase.OpenForTestingAsync(
            options,
            new RuntimeDependencies(failpoints, leaseHeartbeatInterval: TimeSpan.FromHours(1))));
        await failpoints.WaitUntilEnteredAsync(TestTimeouts.Expected);

        var predecessorEpoch = await ReadLeaseEpochAsync(directory.Path);
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, ".midge_leader"),
            $"epoch: {predecessorEpoch}\nholder_id: expired-predecessor\n" +
            $"acquired_at: {DateTimeOffset.UnixEpoch:O}\n");
        using var successorLease = FileLease.Acquire(
            directory.Path,
            predecessorEpoch,
            TimeSpan.Zero,
            null,
            TimeSpan.FromHours(1),
            leaseTimeToLive: TimeSpan.FromHours(2));
        var successorEpoch = successorLease.Epoch;
        Assert.True(successorEpoch > predecessorEpoch);

        failpoints.Release();
        var failure = await Assert.ThrowsAsync<PantsFencedException>(() => open);
        Assert.Equal(PantsErrorCode.Fenced, failure.Code);
        Assert.True(File.Exists(retainedMarker));
        Assert.Equal(successorEpoch, await ReadLeaseEpochAsync(directory.Path));
    }

    [Fact]
    public async Task ShouldFencePendingWalAppendAfterLocalLeaseTakeover()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new FlushPipelineFailpointHandler(Failpoint.BeforeWalAppend);
        var leaseDuration = TimeSpan.FromHours(2);
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithLeaseTimeToLive(leaseDuration)
            .WithLeaseClockSkewTolerance(TimeSpan.Zero);
        var dependencies = new RuntimeDependencies(
            failpoints,
            leaseHeartbeatInterval: TimeSpan.FromHours(1));
        var predecessor = await PantsDatabase.OpenForTestingAsync(options, dependencies);
        var predecessorEpoch = await ReadLeaseEpochAsync(directory.Path);
        await using var transaction = await BeginWriteAsync(predecessor, "predecessor");

        var predecessorCommit = transaction.CommitAsync(PantsWriteOptions.Sync).AsTask();
        await failpoints.WaitUntilEnteredAsync(TestTimeouts.Expected);

        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, ".midge_leader"),
            $"epoch: {predecessorEpoch}\nholder_id: expired-predecessor\n" +
            $"acquired_at: {DateTimeOffset.UnixEpoch:O}\n");
        using var successorLease = FileLease.Acquire(
            directory.Path,
            predecessorEpoch,
            TimeSpan.Zero,
            null,
            TimeSpan.FromHours(1),
            leaseTimeToLive: leaseDuration);
        Assert.True(successorLease.Epoch > predecessorEpoch);

        failpoints.Release();
        var failure = await Assert.ThrowsAsync<PantsFencedException>(() => predecessorCommit);
        Assert.Equal(
            successorLease.Epoch,
            await ReadLeaseEpochAsync(directory.Path));

        Assert.Equal(PantsErrorCode.Fenced, failure!.Code);
        var successorEpoch = successorLease.Epoch;
        successorLease.Dispose();
        await predecessor.DisposeAsync();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, ".midge_leader"),
            $"epoch: {successorEpoch}\nholder_id: released-successor\n" +
            $"acquired_at: {DateTimeOffset.UnixEpoch:O}\n");

        await using var successor = await OpenAsync(directory.Path, new ArmableFailpointHandler());
        await CommitAsync(successor, PantsWriteOptions.Sync, static tx =>
            tx.Put(Key("successor"), Value("authoritative")));
        Assert.Equal("authoritative", await ReadAsync(successor, "successor"));
        Assert.Null(await ReadAsync(successor, "predecessor"));
    }

    [Fact]
    public async Task ShouldRejectManifestJournalAppendAfterLocalLeaseTakeover()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new FlushPipelineFailpointHandler(Failpoint.AfterManifestAuthorityValidation);
        var leaseDuration = TimeSpan.FromHours(2);
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithLeaseTimeToLive(leaseDuration)
            .WithLeaseClockSkewTolerance(TimeSpan.Zero);
        var dependencies = new RuntimeDependencies(
            failpoints,
            leaseHeartbeatInterval: TimeSpan.FromHours(1));
        var predecessor = await PantsDatabase.OpenForTestingAsync(options, dependencies);
        var predecessorEpoch = await ReadLeaseEpochAsync(directory.Path);
        await CommitAsync(predecessor, PantsWriteOptions.Sync, static transaction =>
            transaction.Put(Key("flush-pending"), Value("durable-in-wal")));

        var journalPath = Path.Combine(directory.Path, "manifest.journal");
        var journalLength = new FileInfo(journalPath).Length;
        var predecessorFlush = predecessor.Maintenance.FlushAsync(
            predecessor.ColumnFamilies.DefaultFamily).AsTask();
        await failpoints.WaitUntilEnteredAsync(TestTimeouts.Expected);

        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, ".midge_leader"),
            $"epoch: {predecessorEpoch}\nholder_id: expired-predecessor\n" +
            $"acquired_at: {DateTimeOffset.UnixEpoch:O}\n");
        using var successorLease = FileLease.Acquire(
            directory.Path,
            predecessorEpoch,
            TimeSpan.Zero,
            null,
            TimeSpan.FromHours(1),
            leaseTimeToLive: leaseDuration);
        Assert.True(successorLease.Epoch > predecessorEpoch);

        failpoints.Release();
        var failure = await Assert.ThrowsAsync<PantsFencedException>(() => predecessorFlush);
        Assert.Equal(PantsErrorCode.Fenced, failure.Code);
        Assert.Equal(journalLength, new FileInfo(journalPath).Length);

        var successorEpoch = successorLease.Epoch;
        successorLease.Dispose();
        await predecessor.DisposeAsync();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, ".midge_leader"),
            $"epoch: {successorEpoch}\nholder_id: released-successor\n" +
            $"acquired_at: {DateTimeOffset.UnixEpoch:O}\n");

        await using var reopened = await OpenAsync(directory.Path, new ArmableFailpointHandler());
        Assert.Equal("durable-in-wal", await ReadAsync(reopened, "flush-pending"));
        Assert.Equal(0, (await reopened.Diagnostics.GetRuntimeMetricsAsync()).SstCount);
    }

    [Fact]
    public async Task ShouldRejectIntentLogAppendAfterLocalLeaseTakeover()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new FlushPipelineFailpointHandler(Failpoint.AfterIntentAuthorityValidation);
        var leaseDuration = TimeSpan.FromHours(2);
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithLeaseTimeToLive(leaseDuration)
            .WithLeaseClockSkewTolerance(TimeSpan.Zero);
        var dependencies = new RuntimeDependencies(
            failpoints,
            leaseHeartbeatInterval: TimeSpan.FromHours(1));
        var predecessor = await PantsDatabase.OpenForTestingAsync(options, dependencies);
        var predecessorEpoch = await ReadLeaseEpochAsync(directory.Path);
        await CommitAsync(predecessor, PantsWriteOptions.Sync, static transaction =>
            transaction.Put(Key("intent-pending"), Value("durable-in-wal")));

        var intentPath = Path.Combine(directory.Path, "intent_log.json");
        var intentLength = new FileInfo(intentPath).Length;
        var predecessorFlush = predecessor.Maintenance.FlushAsync(
            predecessor.ColumnFamilies.DefaultFamily).AsTask();
        await failpoints.WaitUntilEnteredAsync(TestTimeouts.Expected);

        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, ".midge_leader"),
            $"epoch: {predecessorEpoch}\nholder_id: expired-predecessor\n" +
            $"acquired_at: {DateTimeOffset.UnixEpoch:O}\n");
        using var successorLease = FileLease.Acquire(
            directory.Path,
            predecessorEpoch,
            TimeSpan.Zero,
            null,
            TimeSpan.FromHours(1),
            leaseTimeToLive: leaseDuration);
        Assert.True(successorLease.Epoch > predecessorEpoch);

        failpoints.Release();
        var failure = await Assert.ThrowsAsync<PantsFencedException>(() => predecessorFlush);
        Assert.Equal(PantsErrorCode.Fenced, failure.Code);
        Assert.Equal(intentLength, new FileInfo(intentPath).Length);

        var successorEpoch = successorLease.Epoch;
        successorLease.Dispose();
        await predecessor.DisposeAsync();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, ".midge_leader"),
            $"epoch: {successorEpoch}\nholder_id: released-successor\n" +
            $"acquired_at: {DateTimeOffset.UnixEpoch:O}\n");

        await using var reopened = await OpenAsync(directory.Path, new ArmableFailpointHandler());
        Assert.Equal("durable-in-wal", await ReadAsync(reopened, "intent-pending"));
        Assert.Equal(0, (await reopened.Diagnostics.GetRuntimeMetricsAsync()).SstCount);
    }

    [Fact]
    public async Task ShouldRejectWalRotationAfterLocalLeaseTakeover()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new FlushPipelineFailpointHandler(Failpoint.AfterWalRotationAuthorityValidation);
        var leaseDuration = TimeSpan.FromHours(2);
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithLeaseTimeToLive(leaseDuration)
            .WithLeaseClockSkewTolerance(TimeSpan.Zero);
        var dependencies = new RuntimeDependencies(
            failpoints,
            leaseHeartbeatInterval: TimeSpan.FromHours(1));
        var predecessor = await PantsDatabase.OpenForTestingAsync(options, dependencies);
        var predecessorEpoch = await ReadLeaseEpochAsync(directory.Path);
        await CommitAsync(predecessor, PantsWriteOptions.Sync, static transaction =>
            transaction.Put(Key("rotation-pending"), Value("durable")));
        var beforeRotation = WalLedgerStore.LoadExisting(Path.Combine(directory.Path, "wal"));

        var predecessorFlush = predecessor.Maintenance.FlushAsync(
            predecessor.ColumnFamilies.DefaultFamily).AsTask();
        await failpoints.WaitUntilEnteredAsync(TestTimeouts.Expected);

        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, ".midge_leader"),
            $"epoch: {predecessorEpoch}\nholder_id: expired-predecessor\n" +
            $"acquired_at: {DateTimeOffset.UnixEpoch:O}\n");
        using var successorLease = FileLease.Acquire(
            directory.Path,
            predecessorEpoch,
            TimeSpan.Zero,
            null,
            TimeSpan.FromHours(1),
            leaseTimeToLive: leaseDuration);
        Assert.True(successorLease.Epoch > predecessorEpoch);

        failpoints.Release();
        var failure = await Assert.ThrowsAsync<PantsFencedException>(() => predecessorFlush);
        Assert.Equal(PantsErrorCode.Fenced, failure.Code);
        var afterRotation = WalLedgerStore.LoadExisting(Path.Combine(directory.Path, "wal"));
        Assert.Equal(beforeRotation.Active.Open.Id, afterRotation.Active.Open.Id);
        Assert.Equal(beforeRotation.NextSegmentId, afterRotation.NextSegmentId);

        var successorEpoch = successorLease.Epoch;
        successorLease.Dispose();
        await predecessor.DisposeAsync();
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, ".midge_leader"),
            $"epoch: {successorEpoch}\nholder_id: released-successor\n" +
            $"acquired_at: {DateTimeOffset.UnixEpoch:O}\n");

        await using var reopened = await OpenAsync(directory.Path, new ArmableFailpointHandler());
        Assert.Equal("durable", await ReadAsync(reopened, "rotation-pending"));
    }

    [Theory]
    [InlineData(nameof(Failpoint.MidWalAppend))]
    [InlineData(nameof(Failpoint.AfterWalAppend))]
    [InlineData(nameof(Failpoint.BeforeWalFlush))]
    [InlineData(nameof(Failpoint.BeforeWalSync))]
    public async Task ShouldFenceWalAndPreserveAcknowledgedRowsGivenAmbiguousWalFailure(
        string failpointName)
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new ArmableFailpointHandler();
        var walPath = WalTestFiles.ActivePath(directory.Path);
        await using (var database = await OpenAsync(directory.Path, failpoints))
        {
            await CommitAsync(database, PantsWriteOptions.Sync, static transaction =>
            {
                transaction.Put(Key("overwritten"), Value("original"));
                transaction.Put(Key("deleted"), Value("kept"));
                transaction.Put(Key("untouched"), Value("steady"));
            });
            var walLength = new FileInfo(walPath).Length;

            failpoints.Arm(
                Enum.Parse<Failpoint>(failpointName),
                static failpoint => new PantsNoSpaceException($"No space at {failpoint}."));
            var unknown = await Assert.ThrowsAsync<PantsCommitOutcomeUnknownException>(() =>
                CommitAsync(database, PantsWriteOptions.Sync, static transaction =>
                {
                    transaction.Put(Key("overwritten"), Value("ghost"));
                    transaction.Delete(Key("deleted"));
                    transaction.Insert(Key("inserted"), Value("ghost"));
                }));
            Assert.Equal(PantsErrorCode.CommitOutcomeUnknown, unknown.Code);
            Assert.Equal(PantsErrorSeverity.OutcomeUnknown, unknown.Severity);
            Assert.IsType<PantsNoSpaceException>(unknown.InnerException);

            Assert.Equal(walLength, new FileInfo(walPath).Length);
            Assert.Equal(
                PantsEngineHealth.Degraded,
                (await database.Diagnostics.GetRuntimeMetricsAsync()).Health);

            foreach (var options in new[] { PantsWriteOptions.Sync, PantsWriteOptions.Buffered })
            {
                var fenced = await Assert.ThrowsAsync<PantsFencedException>(() =>
                    CommitAsync(database, options, static transaction =>
                        transaction.Put(Key("follow-up"), Value("rejected"))));
                Assert.Equal(PantsErrorCode.Fenced, fenced.Code);
            }


            await AssertAcknowledgedRowsAsync(database);
            Assert.Null(await ReadAsync(database, "follow-up"));
        }

        await using (var reopened = await OpenAsync(directory.Path, new ArmableFailpointHandler()))
        {
            await AssertAcknowledgedRowsAsync(reopened);
            Assert.Null(await ReadAsync(reopened, "follow-up"));
            await CommitAsync(reopened, PantsWriteOptions.Sync, static transaction =>
                transaction.Put(Key("after-recovery"), Value("durable")));
        }

        await using var recovered = await OpenAsync(directory.Path, new ArmableFailpointHandler());
        await AssertAcknowledgedRowsAsync(recovered);
        Assert.Equal("durable", await ReadAsync(recovered, "after-recovery"));
    }

    /// <summary>
    ///     A failure proven to precede any WAL byte leaves nothing ambiguous on disk, so only the
    ///     failing commit is rejected and the engine stays healthy and writable.
    /// </summary>
    [Theory]
    [InlineData(nameof(Failpoint.BeforeWalAppend))]
    [InlineData(nameof(Failpoint.BeforeDirectTransactionCommitMarker))]
    public async Task ShouldRejectOnlyFailingCommitGivenFailureBeforeAnyWalByte(string failpointName)
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new ArmableFailpointHandler();
        await using (var database = await OpenAsync(directory.Path, failpoints))
        {
            failpoints.Arm(
                Enum.Parse<Failpoint>(failpointName),
                static failpoint => new PantsNoSpaceException($"No space at {failpoint}."));
            await Assert.ThrowsAsync<PantsNoSpaceException>(() =>
                CommitAsync(database, PantsWriteOptions.Sync, static transaction =>
                    transaction.Put(Key("rejected"), Value("ghost"))));

            Assert.Equal(
                PantsEngineHealth.Healthy,
                (await database.Diagnostics.GetRuntimeMetricsAsync()).Health);
            await CommitAsync(database, PantsWriteOptions.Sync, static transaction =>
                transaction.Put(Key("accepted"), Value("durable")));
        }

        await using var reopened = await OpenAsync(directory.Path, new ArmableFailpointHandler());
        Assert.Null(await ReadAsync(reopened, "rejected"));
        Assert.Equal("durable", await ReadAsync(reopened, "accepted"));
    }

    /// <summary>
    ///     BestEffort never touches the WAL, so a fenced writer does not stand in its way; its
    ///     writes stay visible in memory and carry no durability promise.
    /// </summary>
    [Fact]
    public async Task ShouldAcceptBestEffortCommitWhileWalIsFenced()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new ArmableFailpointHandler();
        await using var database = await OpenAsync(directory.Path, failpoints);
        failpoints.Arm(Failpoint.MidWalAppend);
        await Assert.ThrowsAnyAsync<PantsException>(() =>
            CommitAsync(database, PantsWriteOptions.Sync, static transaction =>
                transaction.Put(Key("torn"), Value("ghost"))));

        await CommitAsync(database, PantsWriteOptions.BestEffort, static transaction =>
            transaction.Put(Key("best-effort"), Value("visible")));

        Assert.Equal("visible", await ReadAsync(database, "best-effort"));
        Assert.Null(await ReadAsync(database, "torn"));
    }

    [Fact]
    public async Task ShouldRejectBothCloudDurabilityModesAfterWalIsFenced()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new ArmableFailpointHandler();
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.SimulatedCloud(directory.Path, "pants-tests", "wal-fencing/")
                .WithBackgroundCompaction(false),
            new RuntimeDependencies(failpoints));
        await CommitAsync(database, PantsWriteOptions.CloudStrict, static transaction =>
            transaction.Put(Key("acknowledged"), Value("durable")));

        failpoints.Arm(Failpoint.MidWalAppend);
        await Assert.ThrowsAnyAsync<PantsException>(() =>
            CommitAsync(database, PantsWriteOptions.CloudAsync, static transaction =>
                transaction.Put(Key("torn"), Value("ghost"))));

        foreach (var options in new[] { PantsWriteOptions.CloudAsync, PantsWriteOptions.CloudStrict })
        {
            var fenced = await Assert.ThrowsAsync<PantsFencedException>(() =>
                CommitAsync(database, options, static transaction =>
                    transaction.Put(Key("follow-up"), Value("rejected"))));
            Assert.Equal(PantsErrorCode.Fenced, fenced.Code);
        }

        Assert.Equal("durable", await ReadAsync(database, "acknowledged"));
        Assert.Null(await ReadAsync(database, "torn"));
        Assert.Null(await ReadAsync(database, "follow-up"));
    }

    /// <summary>
    ///     An explicit durability boundary is an fsync of everything appended so far; if it fails,
    ///     the unsynced suffix is in the same unknown state as a failed commit fsync.
    /// </summary>
    [Fact]
    public async Task ShouldFenceWalGivenBufferedDurabilityBoundarySyncFails()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new ShutdownBoundarySyncFailureFailpointHandler();
        var database = await OpenAsync(directory.Path, failpoints);

        try
        {
            await CommitAsync(database, PantsWriteOptions.Buffered, static transaction =>
                transaction.Put(Key("buffered"), Value("pending")));

            directory.AbandonCleanup();
            var failure = await Assert.ThrowsAnyAsync<PantsException>(() =>
                database.ShutdownAsync(TestTimeouts.Expected).AsTask());

            // The failure is terminal: it is replayed to later callers and new work is rejected.
            var replayed = await Assert.ThrowsAnyAsync<PantsException>(() =>
                database.ShutdownAsync(TestTimeouts.Expected).AsTask());
            Assert.Same(failure, replayed);
            await Assert.ThrowsAsync<PantsBusyException>(() =>
                CommitAsync(database, PantsWriteOptions.Sync, static transaction =>
                    transaction.Put(Key("follow-up"), Value("rejected"))));
        }
        finally
        {
            try
            {
                await database.DisposeAsync();
            }
            catch (PantsException)
            {
                // Disposal replays the permanent shutdown failure by design.
            }
        }
    }

    static async Task AssertAcknowledgedRowsAsync(IPantsDatabase database)
    {
        Assert.Equal("original", await ReadAsync(database, "overwritten"));
        Assert.Equal("kept", await ReadAsync(database, "deleted"));
        Assert.Equal("steady", await ReadAsync(database, "untouched"));
        Assert.Null(await ReadAsync(database, "inserted"));
    }

    static Task<IPantsDatabase> OpenAsync(string path, IFailpointHandler failpoints) =>
        PantsDatabase.OpenForTestingAsync(
                PantsOpenOptions.Local(path).WithBackgroundCompaction(false),
                new RuntimeDependencies(failpoints))
            .AsTask();

    static async Task CommitAsync(
        IPantsDatabase database,
        PantsWriteOptions options,
        Action<IPantsTransaction> stage
    )
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        stage(transaction);
        await transaction.CommitAsync(options);
    }

    static async ValueTask<IPantsTransaction> BeginWriteAsync(IPantsDatabase database, string key)
    {
        var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(Key(key), Value("stale"));
        return transaction;
    }

    static async Task<string?> ReadAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(Key(key));
        return value is null ? null : TestBytes.ToText(value.Value);
    }

    static async Task<ulong> ReadLeaseEpochAsync(string path)
    {
        var text = await File.ReadAllTextAsync(Path.Combine(path, ".midge_leader"));
        var epochLine = text.Split('\n').First(static line => line.StartsWith("epoch: ", StringComparison.Ordinal));
        return ulong.Parse(epochLine.AsSpan("epoch: ".Length), System.Globalization.CultureInfo.InvariantCulture);
    }

    static byte[] Key(string key) => TestBytes.FromString(key);

    static byte[] Value(string value) => TestBytes.FromString(value);
}
