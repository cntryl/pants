namespace Pants.Tests;

public sealed class PantsTransactionContractTests
{
    [Fact]
    public async Task ShouldMakeCommittedWriteVisibleToLaterSnapshot()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using (IPantsTransaction writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put(TestBytes.FromString("key"), TestBytes.FromString("value"));
            await writer.CommitAsync(PantsWriteOptions.Sync);
        }

        await using IPantsTransaction reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        ReadOnlyMemory<byte>? value = await reader.GetAsync(TestBytes.FromString("key"));

        Assert.NotNull(value);
        Assert.Equal("value", TestBytes.ToText(value.Value));
    }

    [Fact]
    public async Task ShouldPreserveFrozenSnapshotAndReadOwnWrites()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsTransaction older = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        Assert.Null(await older.GetAsync(TestBytes.FromString("key")));

        await using (IPantsTransaction newer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            newer.Put(TestBytes.FromString("key"), TestBytes.FromString("committed"));
            await newer.CommitAsync(PantsWriteOptions.Buffered);
        }

        Assert.Null(await older.GetAsync(TestBytes.FromString("key")));
        older.Put(TestBytes.FromString("key"), TestBytes.FromString("mine"));
        ReadOnlyMemory<byte>? own = await older.GetAsync(TestBytes.FromString("key"));
        Assert.Equal("mine", TestBytes.ToText(own!.Value));
    }

    [Fact]
    public async Task ShouldResolveOverlappingWritersByCommitOrderByDefault()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsTransaction first = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        await using IPantsTransaction second = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        first.Put(TestBytes.FromString("key"), TestBytes.FromString("first"));
        second.Put(TestBytes.FromString("key"), TestBytes.FromString("second"));

        await first.CommitAsync(PantsWriteOptions.Sync);
        await second.CommitAsync(PantsWriteOptions.Sync);

        await using IPantsTransaction reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "second",
            TestBytes.ToText((await reader.GetAsync(TestBytes.FromString("key")))!.Value));
    }

    [Fact]
    public async Task ShouldAbortOverlappingWriterWhenStrictConflictPolicyIsSelected()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsTransaction first = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        await using IPantsTransaction second = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        second.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
        first.Put(TestBytes.FromString("key"), TestBytes.FromString("first"));
        second.Put(TestBytes.FromString("key"), TestBytes.FromString("second"));
        await first.CommitAsync(PantsWriteOptions.Sync);

        PantsException error = await Assert.ThrowsAnyAsync<PantsException>(
            () => second.CommitAsync(PantsWriteOptions.Sync).AsTask());

        Assert.Equal(PantsErrorCode.WriteConflict, error.Code);
        Assert.IsType<PantsWriteConflictException>(error);
    }

    [Fact]
    public async Task ShouldValidateAssertionAgainstStartSnapshotAtCommit()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using (IPantsTransaction seed = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            seed.Put(TestBytes.FromString("key"), TestBytes.FromString("old"));
            await seed.CommitAsync(PantsWriteOptions.Sync);
        }

        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString("key"), TestBytes.FromString("new"));
        transaction.AssertValue(TestBytes.FromString("key"), TestBytes.FromString("wrong"));

        PantsException error = await Assert.ThrowsAnyAsync<PantsException>(
            () => transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());
        Assert.Equal(PantsErrorCode.WriteConflict, error.Code);
    }

    [Fact]
    public async Task ShouldApplyLastStagedIntentForPointAndRangeOperations()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put(TestBytes.FromString("a"), TestBytes.FromString("first"));
        transaction.DeleteRange(TestBytes.FromString("a"), TestBytes.FromString("b"));
        transaction.Put(TestBytes.FromString("a"), TestBytes.FromString("second"));

        Assert.Equal(
            "second",
            TestBytes.ToText((await transaction.GetAsync(TestBytes.FromString("a")))!.Value));
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    [Fact]
    public async Task ShouldRejectMutationsInReadOnlyTransaction()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);

        PantsException error = Assert.ThrowsAny<PantsException>(() =>
            transaction.Put(TestBytes.FromString("key"), TestBytes.FromString("value")));

        Assert.Equal(PantsErrorCode.InvalidArgument, error.Code);
    }

    [Fact]
    public async Task ShouldCopyInputsAndOutputsAtOwnershipBoundaries()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        byte[] key = TestBytes.FromString("key");
        byte[] value = TestBytes.FromString("value");
        await using (IPantsTransaction writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put(key, value);
            key[0] = (byte)'X';
            value[0] = (byte)'X';
            await writer.CommitAsync(PantsWriteOptions.Sync);
        }

        await using IPantsTransaction reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        ReadOnlyMemory<byte>? observed = await reader.GetAsync(TestBytes.FromString("key"));
        byte[] callerOwned = observed!.Value.ToArray();
        callerOwned[0] = (byte)'X';

        Assert.Equal("value", TestBytes.ToText((await reader.GetAsync(TestBytes.FromString("key")))!.Value));
    }

    [Fact]
    public async Task ShouldRequireTtlToHaveWholeSecondPrecision()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);

        PantsException fractional = Assert.ThrowsAny<PantsException>(() => transaction.Put(
            TestBytes.FromString("key"),
            TestBytes.FromString("value"),
            TimeSpan.FromMilliseconds(1500)));
        PantsException negative = Assert.ThrowsAny<PantsException>(() => transaction.Put(
            TestBytes.FromString("key"),
            TestBytes.FromString("value"),
            TimeSpan.FromSeconds(-1)));

        Assert.Equal(PantsErrorCode.InvalidArgument, fractional.Code);
        Assert.Equal(PantsErrorCode.InvalidArgument, negative.Code);
    }

    [Fact]
    public async Task ShouldStartEveryStagedTtlAtOneCommitTime()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.InMemory().WithTtlClock(clock));
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("a"u8.ToArray(), "a"u8.ToArray(), TimeSpan.FromSeconds(1));
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        transaction.Put("b"u8.ToArray(), "b"u8.ToArray(), TimeSpan.FromSeconds(1));

        await transaction.CommitAsync(PantsWriteOptions.Buffered);
        clock.UtcNow = clock.UtcNow.AddSeconds(1);

        await using IPantsTransaction reader = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync("a"u8.ToArray()));
        Assert.Null(await reader.GetAsync("b"u8.ToArray()));
    }

    [Fact]
    public async Task ShouldKeepPendingTtlVisibleUntilCommit()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.InMemory().WithTtlClock(clock));
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.Put("key"u8.ToArray(), "value"u8.ToArray(), TimeSpan.FromSeconds(1));
        clock.UtcNow = clock.UtcNow.AddDays(1);

        ReadOnlyMemory<byte>? value = await transaction.GetAsync("key"u8.ToArray());

        Assert.Equal("value", TestBytes.ToText(value!.Value));
    }

    [Fact]
    public async Task ShouldNotReExposeExpiredValueWhenClockMovesBackward()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.InMemory().WithTtlClock(clock));
        await using (IPantsTransaction writer = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            writer.Put("key"u8.ToArray(), "value"u8.ToArray(), TimeSpan.FromSeconds(1));
            await writer.CommitAsync(PantsWriteOptions.Buffered);
        }

        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        await using (IPantsTransaction expired = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadOnly))
        {
            Assert.Null(await expired.GetAsync("key"u8.ToArray()));
        }

        clock.UtcNow = DateTimeOffset.UnixEpoch.AddMilliseconds(500);
        await using IPantsTransaction afterSkew = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await afterSkew.GetAsync("key"u8.ToArray()));
    }

    [Fact]
    public async Task ShouldAbortPointWriteCoveredByRecentRangeDeleteInStrictMode()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsTransaction pointWriter = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        pointWriter.SetConflictPolicy(PantsConflictPolicy.AbortOnWriteConflict);
        pointWriter.Put("middle"u8.ToArray(), "value"u8.ToArray());

        await using (IPantsTransaction rangeWriter = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            rangeWriter.DeleteRange("a"u8.ToArray(), "z"u8.ToArray());
            await rangeWriter.CommitAsync(PantsWriteOptions.Buffered);
        }

        PantsException error = await Assert.ThrowsAnyAsync<PantsException>(
            () => pointWriter.CommitAsync(PantsWriteOptions.Buffered).AsTask());
        Assert.Equal(PantsErrorCode.WriteConflict, error.Code);
    }

    [Fact]
    public async Task ShouldEnforceEveryAssertionWhenTheSameKeyIsAssertedTwice()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using (IPantsTransaction seed = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            seed.Put("key"u8.ToArray(), "value"u8.ToArray());
            await seed.CommitAsync(PantsWriteOptions.Buffered);
        }

        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        transaction.AssertValue("key"u8.ToArray(), "value"u8.ToArray());
        transaction.AssertValue("key"u8.ToArray(), "different"u8.ToArray());

        PantsException error = await Assert.ThrowsAnyAsync<PantsException>(() =>
            transaction.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        Assert.Equal(PantsErrorCode.WriteConflict, error.Code);
    }

    [Fact]
    public async Task ShouldAbortAssertionCoveredByARecentRangeDelete()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using (IPantsTransaction seed = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            seed.Put("middle"u8.ToArray(), "value"u8.ToArray());
            await seed.CommitAsync(PantsWriteOptions.Buffered);
        }

        await using IPantsTransaction asserting = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadWrite);
        asserting.AssertValue("middle"u8.ToArray(), "value"u8.ToArray());
        await using (IPantsTransaction deleting = await database.BeginTransactionAsync(
                         database.DefaultColumnFamily,
                         PantsTransactionMode.ReadWrite))
        {
            deleting.DeleteRange("a"u8.ToArray(), "z"u8.ToArray());
            await deleting.CommitAsync(PantsWriteOptions.Buffered);
        }

        PantsException error = await Assert.ThrowsAnyAsync<PantsException>(() =>
            asserting.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        Assert.Equal(PantsErrorCode.WriteConflict, error.Code);
    }

    [Fact]
    public async Task ShouldReleaseSnapshotWhenRollbackIsCanceledBeforeAdmission()
    {
        await using IPantsDatabase database = await PantsDatabase.OpenAsync(PantsOpenOptions.InMemory());
        await using IPantsTransaction transaction = await database.BeginTransactionAsync(
            database.DefaultColumnFamily,
            PantsTransactionMode.ReadOnly);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            transaction.RollbackAsync(cancellation.Token).AsTask());

        Assert.Equal(0, (await database.GetRuntimeMetricsAsync()).ActiveSnapshots);
    }
}
