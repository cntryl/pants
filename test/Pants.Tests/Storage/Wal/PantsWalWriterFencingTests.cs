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
        var walPath = Path.Combine(directory.Path, "wal", "wal.log");
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
            await Assert.ThrowsAsync<PantsNoSpaceException>(() =>
                CommitAsync(database, PantsWriteOptions.Sync, static transaction =>
                {
                    transaction.Put(Key("overwritten"), Value("ghost"));
                    transaction.Delete(Key("deleted"));
                    transaction.Insert(Key("inserted"), Value("ghost"));
                }));

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
            failpoints.Arm(Enum.Parse<Failpoint>(failpointName));
            await Assert.ThrowsAnyAsync<PantsException>(() =>
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

    /// <summary>
    ///     An explicit durability boundary is an fsync of everything appended so far; if it fails,
    ///     the unsynced suffix is in the same unknown state as a failed commit fsync.
    /// </summary>
    [Fact]
    public async Task ShouldFenceWalGivenBufferedDurabilityBoundarySyncFails()
    {
        using var directory = new TemporaryDirectory();
        var failpoints = new ArmableFailpointHandler();
        await using var database = await OpenAsync(directory.Path, failpoints);
        await CommitAsync(database, PantsWriteOptions.Buffered, static transaction =>
            transaction.Put(Key("buffered"), Value("pending")));

        failpoints.Arm(Failpoint.BeforeWalSync);
        await Assert.ThrowsAnyAsync<PantsException>(() => database.ShutdownAsync(TimeSpan.FromSeconds(10)).AsTask());

        Assert.Equal(
            PantsEngineHealth.Degraded,
            (await database.Diagnostics.GetRuntimeMetricsAsync()).Health);
        await Assert.ThrowsAsync<PantsFencedException>(() =>
            CommitAsync(database, PantsWriteOptions.Sync, static transaction =>
                transaction.Put(Key("follow-up"), Value("rejected"))));
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
        Action<IPantsTransaction> stage)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        stage(transaction);
        await transaction.CommitAsync(options);
    }

    static async Task<string?> ReadAsync(IPantsDatabase database, string key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(Key(key));
        return value is null ? null : TestBytes.ToText(value.Value);
    }

    static byte[] Key(string key) => TestBytes.FromString(key);

    static byte[] Value(string value) => TestBytes.FromString(value);
}
