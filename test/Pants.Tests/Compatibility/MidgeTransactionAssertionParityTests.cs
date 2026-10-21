using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Compatibility;

public sealed class MidgeTransactionAssertionParityTests
{
    [Fact]
    public async Task ShouldAssertExpectedValueWithoutCreatingAWriteConflict()
    {
        await using var database = await OpenMemoryAsync();
        var family = await database.ColumnFamilies.CreateAsync("assertions");
        await PutAsync(database, family, "key", "value");

        await using (var existing = await BeginWriteAsync(database, family))
        {
            existing.AssertValue("key"u8.ToArray(), "value"u8.ToArray());
            await existing.CommitAsync(PantsWriteOptions.Buffered);
        }

        await using var absent = await BeginWriteAsync(database, family);
        absent.AssertValue("absent"u8.ToArray(), null);
        await absent.CommitAsync(PantsWriteOptions.Buffered);
    }

    [Fact]
    public async Task ShouldRejectAssertionWhenSnapshotValueDiffers()
    {
        await using var database = await OpenMemoryAsync();
        var family = await database.ColumnFamilies.CreateAsync("assertions");
        await PutAsync(database, family, "key", "actual");
        await using var transaction = await BeginWriteAsync(database, family);
        transaction.AssertValue("key"u8.ToArray(), "expected"u8.ToArray());

        var conflict = await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
            transaction.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        Assert.Equal(PantsErrorCode.WriteConflict, conflict.Code);
    }

    [Fact]
    public async Task ShouldValidateAssertionAgainstSnapshotWhenKeyIsWritten()
    {
        await using var database = await OpenMemoryAsync();
        var family = await database.ColumnFamilies.CreateAsync("assertion_snapshot");
        await PutAsync(database, family, "key", "old");

        await using (var transaction = await BeginWriteAsync(database, family))
        {
            transaction.Put("key"u8.ToArray(), "new"u8.ToArray());
            transaction.AssertValue("key"u8.ToArray(), "old"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        Assert.Equal("new", await ReadAsync(database, family, "key"));
    }

    [Fact]
    public async Task ShouldUseTransactionSnapshotTimeWhenAssertedValueExpiresBeforeCommit()
    {
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeMilliseconds(10_000));
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.InMemory().WithTtlClock(clock));
        var family = await database.ColumnFamilies.CreateAsync("assertion-ttl");
        await using (var seed = await BeginWriteAsync(database, family))
        {
            seed.Put("ttl-key"u8.ToArray(), "value"u8.ToArray(), TimeSpan.FromSeconds(1));
            await seed.CommitAsync(PantsWriteOptions.Buffered);
        }

        await using (var asserting = await BeginWriteAsync(database, family))
        {
            asserting.AssertValue("ttl-key"u8.ToArray(), "value"u8.ToArray());
            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            await asserting.CommitAsync(PantsWriteOptions.Buffered);
        }

        Assert.Null(await ReadAsync(database, family, "ttl-key"));
    }

    [Fact]
    public async Task ShouldIsolateAssertionConflictsBetweenColumnFamilies()
    {
        await using var database = await OpenMemoryAsync();
        var first = await database.ColumnFamilies.CreateAsync("assertion-cf-first");
        var second = await database.ColumnFamilies.CreateAsync("assertion-cf-second");
        await PutAsync(database, first, "shared-key", "first");
        await PutAsync(database, second, "shared-key", "second");

        await using var asserting = await BeginWriteAsync(database, first);
        asserting.AssertValue("shared-key"u8.ToArray(), "first"u8.ToArray());
        await PutAsync(database, second, "shared-key", "updated");

        await asserting.CommitAsync(PantsWriteOptions.Buffered);

        Assert.Equal("first", await ReadAsync(database, first, "shared-key"));
        Assert.Equal("updated", await ReadAsync(database, second, "shared-key"));
    }

    [Fact]
    public async Task ShouldRejectConflictingDuplicateAssertionsForOneKey()
    {
        await using var database = await OpenMemoryAsync();
        var family = await database.ColumnFamilies.CreateAsync("duplicate-assertion");
        await PutAsync(database, family, "key", "value");
        await using var asserting = await BeginWriteAsync(database, family);
        asserting.AssertValue("key"u8.ToArray(), "value"u8.ToArray());
        asserting.AssertValue("key"u8.ToArray(), "different"u8.ToArray());

        var conflict = await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
            asserting.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        Assert.Equal(PantsErrorCode.WriteConflict, conflict.Code);
    }

    [Fact]
    public async Task ShouldRejectAssertionWhenConcurrentPutLandsAfterStartSequence()
    {
        await using var database = await OpenMemoryAsync();
        var family = await database.ColumnFamilies.CreateAsync("assert-put");
        await PutAsync(database, family, "key", "v1");
        await using var asserting = await BeginWriteAsync(database, family);
        asserting.AssertValue("key"u8.ToArray(), "v1"u8.ToArray());
        asserting.Put("other"u8.ToArray(), "unrelated"u8.ToArray());
        await PutAsync(database, family, "key", "v2");
        var sequenceBefore = (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence;

        var conflict = await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
            asserting.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        Assert.Equal(PantsErrorCode.WriteConflict, conflict.Code);
        Assert.Equal(sequenceBefore, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
        Assert.Null(await ReadAsync(database, family, "other"));
        Assert.Equal("v2", await ReadAsync(database, family, "key"));
    }

    [Fact]
    public async Task ShouldRejectAssertionWhenConcurrentDeleteLandsAfterStartSequence()
    {
        await using var database = await OpenMemoryAsync();
        var family = await database.ColumnFamilies.CreateAsync("assert-delete");
        await PutAsync(database, family, "key", "v1");
        await using var asserting = await BeginWriteAsync(database, family);
        asserting.AssertValue("key"u8.ToArray(), "v1"u8.ToArray());
        await using (var deleting = await BeginWriteAsync(database, family))
        {
            deleting.Delete("key"u8.ToArray());
            await deleting.CommitAsync(PantsWriteOptions.Buffered);
        }

        var conflict = await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
            asserting.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        Assert.Equal(PantsErrorCode.WriteConflict, conflict.Code);
        Assert.Null(await ReadAsync(database, family, "key"));
    }

    [Fact]
    public async Task ShouldRejectAssertionWhenConcurrentRangeDeleteCoversTheKey()
    {
        await using var database = await OpenMemoryAsync();
        var family = await database.ColumnFamilies.CreateAsync("assert-range-delete");
        await PutAsync(database, family, "key-b", "v1");
        await using var asserting = await BeginWriteAsync(database, family);
        asserting.AssertValue("key-b"u8.ToArray(), "v1"u8.ToArray());
        await using (var deleting = await BeginWriteAsync(database, family))
        {
            deleting.DeleteRange("key-a"u8.ToArray(), "key-z"u8.ToArray());
            await deleting.CommitAsync(PantsWriteOptions.Buffered);
        }

        var conflict = await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
            asserting.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        Assert.Equal(PantsErrorCode.WriteConflict, conflict.Code);
        Assert.Null(await ReadAsync(database, family, "key-b"));
    }

    [Fact]
    public async Task ShouldRejectAbsentAssertionWhenKeyIsInsertedConcurrently()
    {
        await using var database = await OpenMemoryAsync();
        var family = await database.ColumnFamilies.CreateAsync("assert-absent-insert");
        await using var asserting = await BeginWriteAsync(database, family);
        asserting.AssertValue("key"u8.ToArray(), null);
        await PutAsync(database, family, "key", "inserted");

        var conflict = await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
            asserting.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        Assert.Equal(PantsErrorCode.WriteConflict, conflict.Code);
        Assert.Equal("inserted", await ReadAsync(database, family, "key"));
    }

    [Fact]
    public async Task ShouldRejectAssertionGivenAbaValueRestoredAfterInterveningWrite()
    {
        await using var database = await OpenMemoryAsync();
        var family = await database.ColumnFamilies.CreateAsync("assert-aba");
        await PutAsync(database, family, "key", "v1");
        await using var asserting = await BeginWriteAsync(database, family);
        asserting.AssertValue("key"u8.ToArray(), "v1"u8.ToArray());
        await PutAsync(database, family, "key", "v2");
        await PutAsync(database, family, "key", "v1");

        var conflict = await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
            asserting.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        Assert.Equal(PantsErrorCode.WriteConflict, conflict.Code);
        Assert.Equal("v1", await ReadAsync(database, family, "key"));
    }

    [Fact]
    public async Task ShouldAllowWritingAssertedKeyInSameTransaction()
    {
        await using var database = await OpenMemoryAsync();
        var family = await database.ColumnFamilies.CreateAsync("assert-self");
        await PutAsync(database, family, "key", "v1");

        await using (var transaction = await BeginWriteAsync(database, family))
        {
            transaction.AssertValue("key"u8.ToArray(), "v1"u8.ToArray());
            transaction.Put("key"u8.ToArray(), "v2"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        Assert.Equal("v2", await ReadAsync(database, family, "key"));
    }

    [Fact]
    public async Task ShouldCommitDisjointAssertionWithWrite()
    {
        await using var database = await OpenMemoryAsync();
        var family = await database.ColumnFamilies.CreateAsync("assert-disjoint");
        await PutAsync(database, family, "guard", "unchanged");
        await using (var transaction = await BeginWriteAsync(database, family))
        {
            transaction.AssertValue("guard"u8.ToArray(), "unchanged"u8.ToArray());
            transaction.Put("data"u8.ToArray(), "value"u8.ToArray());
            await transaction.CommitAsync(PantsWriteOptions.Buffered);
        }

        Assert.Equal("value", await ReadAsync(database, family, "data"));
    }

    [Fact]
    public async Task ShouldEnforceAssertionConflictEvenUnderLastWriteWinsPolicy()
    {
        await using var database = await OpenMemoryAsync();
        var family = await database.ColumnFamilies.CreateAsync("assert-lww");
        await PutAsync(database, family, "key", "v1");
        await using var asserting = await BeginWriteAsync(database, family);
        asserting.SetConflictPolicy(PantsConflictPolicy.LastWriteWins);
        asserting.AssertValue("key"u8.ToArray(), "v1"u8.ToArray());
        asserting.Put("other"u8.ToArray(), "value"u8.ToArray());
        await PutAsync(database, family, "key", "v2");

        var conflict = await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
            asserting.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        Assert.Equal(PantsErrorCode.WriteConflict, conflict.Code);
        Assert.Null(await ReadAsync(database, family, "other"));
    }

    [Fact]
    public async Task ShouldValidateAssertionOnlyCommitWithoutAllocatingASequence()
    {
        await using var database = await OpenMemoryAsync();
        var family = await database.ColumnFamilies.CreateAsync("assert-only-commit");
        await PutAsync(database, family, "key", "v1");
        await using var transaction = await BeginWriteAsync(database, family);
        transaction.AssertValue("key"u8.ToArray(), "v1"u8.ToArray());
        var sequenceBefore = (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence;

        await transaction.CommitAsync(PantsWriteOptions.Buffered);

        Assert.Equal(sequenceBefore, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
    }

    [Fact]
    public async Task ShouldRejectAssertionOnlyCommitWhenKeyChangedConcurrently()
    {
        await using var database = await OpenMemoryAsync();
        var family = await database.ColumnFamilies.CreateAsync("assert-only-reject");
        await PutAsync(database, family, "key", "v1");
        await using var asserting = await BeginWriteAsync(database, family);
        asserting.AssertValue("key"u8.ToArray(), "v1"u8.ToArray());
        await PutAsync(database, family, "key", "v2");
        var sequenceBefore = (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence;

        var conflict = await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
            asserting.CommitAsync(PantsWriteOptions.Buffered).AsTask());

        Assert.Equal(PantsErrorCode.WriteConflict, conflict.Code);
        Assert.Equal(sequenceBefore, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
    }

    [Fact]
    public async Task ShouldRejectAssertionConflictInASpilledTransaction()
    {
        foreach (var mode in new[] { "local", "cloud" })
        {
            using var directory = new TemporaryDirectory();
            await using var database = await OpenDurableAsync(mode, directory.Path, transactionPoolBytes: 8 * 1024);
            var family = await database.ColumnFamilies.CreateAsync("assert-spill");
            await PutAsync(database, family, "guard", "v1", mode);
            await using var asserting = await BeginWriteAsync(database, family);
            asserting.AssertValue("guard"u8.ToArray(), "v1"u8.ToArray());
            for (var index = 0; index < 200; index++)
            {
                asserting.Put(
                    TestBytes.FromString($"spill-key{index:0000}"),
                    TestBytes.FromString($"spill-value_{index:0000}"));
            }

            Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));
            await PutAsync(database, family, "guard", "v2", mode);
            var sequenceBefore = (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence;

            var conflict = await Assert.ThrowsAsync<PantsWriteConflictException>(() =>
                asserting.CommitAsync(WriteOptions(mode)).AsTask());

            Assert.Equal(PantsErrorCode.WriteConflict, conflict.Code);
            Assert.Equal(sequenceBefore, (await database.Diagnostics.GetRuntimeMetricsAsync()).CurrentSequence);
            for (var index = 0; index < 200; index++)
            {
                Assert.Null(await ReadAsync(database, family, $"spill-key{index:0000}"));
            }

            Assert.Equal("v2", await ReadAsync(database, family, "guard"));
        }
    }

    [Fact]
    public async Task ShouldBoundAssertionMemoryByTransactionPool()
    {
        await using var database = await OpenMemoryAsync(transactionPoolBytes: 512);
        var family = await database.ColumnFamilies.CreateAsync("assertion_limit");
        await using var transaction = await BeginWriteAsync(database, family);

        transaction.AssertValue(FillKey((byte)'a', 256), null);
        var error = Assert.Throws<PantsResourceLimitException>(() =>
            transaction.AssertValue(FillKey((byte)'b', 256), null));

        Assert.Equal(PantsErrorCode.ResourceLimit, error.Code);
    }

    [Fact]
    public async Task ShouldRejectAssertionWhenWriteIntentConsumesSharedTransactionPool()
    {
        await using var database = await OpenMemoryAsync(transactionPoolBytes: 1_024);
        var family = await database.ColumnFamilies.CreateAsync("write-pressure");
        await using var asserting = await BeginWriteAsync(database, family);
        PantsResourceLimitException error;
        await using (var writer = await BeginWriteAsync(database, family))
        {
            writer.Put("held"u8.ToArray(), "value"u8.ToArray());
            error = Assert.Throws<PantsResourceLimitException>(() =>
                asserting.AssertValue(FillKey((byte)'a', 904), null));
        }

        Assert.Equal(PantsErrorCode.ResourceLimit, error.Code);
        asserting.AssertValue(FillKey((byte)'b', 904), null);
    }

    [Fact]
    public async Task ShouldRejectWriteIntentWhenAssertionConsumesSharedTransactionPool()
    {
        await using var database = await OpenMemoryAsync(transactionPoolBytes: 1_024);
        var family = await database.ColumnFamilies.CreateAsync("assertion-pressure");
        await using var writer = await BeginWriteAsync(database, family);
        PantsResourceLimitException error;
        await using (var asserting = await BeginWriteAsync(database, family))
        {
            asserting.AssertValue(FillKey((byte)'a', 976), null);
            error = Assert.Throws<PantsResourceLimitException>(() =>
                writer.Put("held"u8.ToArray(), "value"u8.ToArray()));
        }

        Assert.Equal(PantsErrorCode.ResourceLimit, error.Code);
        writer.Put("released"u8.ToArray(), "value"u8.ToArray());
        await writer.CommitAsync(PantsWriteOptions.Buffered);
        Assert.Equal("value", await ReadAsync(database, family, "released"));
    }

    [Fact]
    public async Task ShouldSpillWriteIntentWhenAssertionConsumesSharedPoolInLocalMode()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await OpenDurableAsync(
            "local",
            directory.Path,
            transactionPoolBytes: 1_024);
        var family = await database.ColumnFamilies.CreateAsync("assertion-spill-pressure");
        await using var asserting = await BeginWriteAsync(database, family);
        asserting.AssertValue(FillKey((byte)'a', 976), null);
        await using var writer = await BeginWriteAsync(database, family);

        writer.Put("spilled"u8.ToArray(), "value"u8.ToArray());

        Assert.NotEmpty(Directory.GetFiles(Path.Combine(directory.Path, "txn"), "*.run"));
        await writer.CommitAsync(PantsWriteOptions.Buffered);
        Assert.Equal("value", await ReadAsync(database, family, "spilled"));
    }

    static ValueTask<IPantsDatabase> OpenMemoryAsync(long? transactionPoolBytes = null)
    {
        var options = PantsOpenOptions.InMemory();
        if (transactionPoolBytes is { } bytes)
        {
            options = options.WithTransactionMemoryPool(bytes);
        }

        return PantsDatabase.OpenAsync(options);
    }

    static ValueTask<IPantsDatabase> OpenDurableAsync(
        string mode,
        string path,
        long? transactionPoolBytes = null)
    {
        var options = mode switch
        {
            "local" => PantsOpenOptions.Local(path),
            "cloud" => PantsOpenOptions.SimulatedCloud(path, "pants-tests", "midge-assertions/"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown durable mode.")
        };
        options = options.WithBackgroundCompaction(false);
        if (transactionPoolBytes is { } bytes)
        {
            options = options.WithTransactionMemoryPool(bytes);
        }

        return PantsDatabase.OpenAsync(options);
    }

    static ValueTask<IPantsTransaction> BeginWriteAsync(
        IPantsDatabase database,
        IPantsColumnFamily family) =>
        database.Transactions.BeginAsync(family, PantsTransactionMode.ReadWrite);

    static async Task PutAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key,
        string value,
        string mode = "memory")
    {
        await using var transaction = await BeginWriteAsync(database, family);
        transaction.Put(TestBytes.FromString(key), TestBytes.FromString(value));
        await transaction.CommitAsync(WriteOptions(mode));
    }

    static PantsWriteOptions WriteOptions(string mode) =>
        mode == "cloud" ? PantsWriteOptions.CloudAsync : PantsWriteOptions.Buffered;

    static byte[] FillKey(byte value, int length) => Enumerable.Repeat(value, length).ToArray();

    static async Task<string?> ReadAsync(
        IPantsDatabase database,
        IPantsColumnFamily family,
        string key)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        var value = await transaction.GetAsync(TestBytes.FromString(key));
        return value is { } present ? TestBytes.ToText(present) : null;
    }
}
