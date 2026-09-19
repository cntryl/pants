using Cntryl.Pants.Storage.Internal.Wal;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsSstEntryAdmissionTests
{
    /// <summary>
    ///     Prefix compression cannot be relied on for admission: flush or compaction may place any
    ///     entry first in a block, where it carries its key in full. The boundary is therefore the
    ///     worst-case encoded size, including the wider header a long key forces.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(ushort.MaxValue)]
    [InlineData(ushort.MaxValue + 1)]
    public void ShouldAdmitOnlyEntriesWithinTheDecodedBlockLimit(int keyLength)
    {
        var header = keyLength > ushort.MaxValue ? 34 : 26;
        var valueLength = DiskFormat.MaximumDecodedBlockBytes - header - keyLength;

        SstCodec.ValidateEntrySize(keyLength, valueLength);

        Assert.Throws<PantsResourceLimitException>(() =>
            SstCodec.ValidateEntrySize(keyLength, valueLength + 1));
    }

    [Fact]
    public void ShouldRejectEntrySizeGivenLengthsOverflow()
    {
        Assert.Throws<PantsResourceLimitException>(() =>
            SstCodec.ValidateEntrySize(int.MaxValue, 1));
        Assert.Throws<PantsResourceLimitException>(() =>
            SstCodec.ValidateEntrySize(1, int.MaxValue));
    }

    /// <summary>
    ///     Rejection has to happen where the caller staged the write, not later at flush: accepting
    ///     data the engine cannot subsequently write down surfaces the failure detached from its
    ///     cause, on a background path the caller cannot handle.
    /// </summary>
    [Fact]
    public async Task ShouldRejectOversizedValueWhenStagedAndStillAcceptLaterCommit()
    {
        using var directory = new TemporaryDirectory();
        await using var database = await PantsDatabase.OpenAsync(
            PantsOpenOptions.Local(directory.Path).WithBackgroundCompaction(false));
        var family = database.ColumnFamilies.DefaultFamily;

        await using (var transaction = await database.Transactions.BeginAsync(
                         family,
                         PantsTransactionMode.ReadWrite))
        {
            Assert.Throws<PantsResourceLimitException>(() =>
                transaction.Put(
                    TestBytes.FromString("oversized"),
                    new byte[DiskFormat.MaximumDecodedBlockBytes]));

            transaction.Put(TestBytes.FromString("kept"), TestBytes.FromString("value"));
            await transaction.CommitAsync(PantsWriteOptions.Sync);
        }

        await using var reader = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync(TestBytes.FromString("oversized")));
        Assert.Equal(
            "value",
            TestBytes.ToText((await reader.GetAsync(TestBytes.FromString("kept")))!.Value));
    }

    /// <summary>
    ///     A point tombstone carries its key into both the WAL and every SST that holds it, so an
    ///     unencodable key must be refused where it is staged. Admitting it acknowledges a commit
    ///     that no flush can write down and no replay can read back.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldRejectOversizedDeleteKeyWhenStagedAndKeepTransactionUsable(bool spilled)
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path, spilled);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = database.ColumnFamilies.DefaultFamily;
            var pool = Assert.IsType<DatabaseInstance>(database).TransactionMemoryPool;
            await using (var transaction = await database.Transactions.BeginAsync(
                             family,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Put(TestBytes.FromString("kept"), new byte[spilled ? 900 : 5]);
                var poolBefore = pool.Used;

                Assert.Throws<PantsResourceLimitException>(() =>
                    transaction.Delete(new byte[DiskFormat.MaximumDecodedBlockBytes + 16]));

                Assert.Equal(poolBefore, pool.Used);
                transaction.Put(TestBytes.FromString("later"), TestBytes.FromString("value"));
                await transaction.CommitAsync(PantsWriteOptions.Sync);
            }

            await database.Maintenance.FlushAsync(family);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.NotNull(await reader.GetAsync(TestBytes.FromString("kept")));
        Assert.Equal(
            "value",
            TestBytes.ToText((await reader.GetAsync(TestBytes.FromString("later")))!.Value));
    }

    /// <summary>
    ///     The largest key a Delete admits must fit the WAL under either framing a commit may
    ///     choose - one batch record for a resident transaction, a record per operation once it
    ///     has spilled - and replay after reopen.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldCommitAndReplayLargestAdmissibleDeleteKey(bool spilled)
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path, spilled);
        var key = CreateLargestAdmissibleDeleteKey();
        await CommitDeleteAsync(options, key);

        await using var replayed = await PantsDatabase.OpenAsync(options);
        await using var reader = await replayed.Transactions.BeginAsync(
            replayed.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync(key));
    }

    /// <summary>
    ///     Flush writes the same key into a data block, the index and the metadata key range; the
    ///     last two exceed the decoded block limit and must still be readable after reopen.
    /// </summary>
    [Fact]
    public async Task ShouldFlushAndReopenLargestAdmissibleDeleteKey()
    {
        using var directory = new TemporaryDirectory();
        var options = CreateOptions(directory.Path, false);
        var key = CreateLargestAdmissibleDeleteKey();
        await CommitDeleteAsync(options, key);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await database.Maintenance.FlushAsync(database.ColumnFamilies.DefaultFamily);
        }

        Assert.NotEmpty(Directory.GetFiles(directory.Path, "*.sst", SearchOption.AllDirectories));
        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync(key));
    }

    [Fact]
    public void ShouldRejectUnencodableEntryBeforeWritingSstBytes()
    {
        using var output = new MemoryStream();
        SstEntry[] entries =
        [
            new(TestBytes.FromString("a"), TestBytes.FromString("value"), 1, null, false),
            new(new byte[DiskFormat.MaximumDecodedBlockBytes], null, 2, null, true)
        ];

        Assert.Throws<PantsResourceLimitException>(() =>
            SstCodec.EncodeTo(output, entries, [], PantsPerformanceGoal.Latency));

        Assert.Equal(0, output.Length);
    }

    /// <summary>
    ///     The WAL must never append a record its own replay would reject. A compressible batch can
    ///     fit a frame yet decode past the decompression limit, so the encoder refuses it instead.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldNotEncodeWalRecordThatReplayWouldReject(bool carriesValue)
    {
        var operation = carriesValue ? WalOperation.Put : WalOperation.Delete;
        WalMutation[] mutations =
        [
            new(
                0,
                operation,
                new byte[DiskFormat.MaximumDecodedBlockBytes / 2],
                carriesValue ? new byte[DiskFormat.MaximumDecodedBlockBytes / 2] : null,
                1,
                null,
                null),
            new(0, WalOperation.Delete, new byte[DiskFormat.MaximumDecodedBlockBytes / 2], null, 2, null, null)
        ];

        Assert.Throws<PantsResourceLimitException>(() =>
            WalCodec.EncodeTransactionBatch(1, 1, 1, mutations));
        Assert.Throws<PantsResourceLimitException>(() =>
            WalCodec.EncodeTransactionMutation(
                mutations[0] with { Key = new byte[DiskFormat.MaximumDecodedBlockBytes] },
                1,
                1));
    }

    /// <summary>
    ///     A resident transaction whose batch cannot be framed must fail its own commit before any
    ///     WAL bytes are written, rather than acknowledge a record that bricks the next open.
    /// </summary>
    [Fact]
    public async Task ShouldRejectUnframeableResidentCommitAndRemainUsable()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(1024L * 1024 * 1024))
            .WithTransactionMemoryPool(512L * 1024 * 1024);
        var half = DiskFormat.MaximumDecodedBlockBytes / 2;
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            var family = database.ColumnFamilies.DefaultFamily;
            await using (var transaction = await database.Transactions.BeginAsync(
                             family,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Put(TestBytes.FromString("first"), new byte[half]);
                transaction.Put(TestBytes.FromString("second"), new byte[half]);

                await Assert.ThrowsAsync<PantsResourceLimitException>(() =>
                    transaction.CommitAsync(PantsWriteOptions.Sync).AsTask());
            }

            await using (var transaction = await database.Transactions.BeginAsync(
                             family,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.Put(TestBytes.FromString("later"), TestBytes.FromString("value"));
                await transaction.CommitAsync(PantsWriteOptions.Sync);
            }
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await using var reader = await reopened.Transactions.BeginAsync(
            reopened.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        Assert.Null(await reader.GetAsync(TestBytes.FromString("first")));
        Assert.Equal(
            "value",
            TestBytes.ToText((await reader.GetAsync(TestBytes.FromString("later")))!.Value));
    }

    /// <summary>
    ///     An unframeable transaction must not share a coalesced WAL group: its encoding failure
    ///     would otherwise roll back and fail every well-formed commit batched alongside it.
    /// </summary>
    [Fact]
    public async Task ShouldNotFailCoalescedNeighbourGivenUnframeableResidentCommit()
    {
        using var directory = new TemporaryDirectory();
        using var failpoints = new CoalescedCommitFailureFailpointHandler();
        var half = DiskFormat.MaximumDecodedBlockBytes / 2;
        await using var database = await PantsDatabase.OpenForTestingAsync(
            PantsOpenOptions.Local(directory.Path)
                .WithBackgroundCompaction(false)
                .WithMemoryBudget(PantsMemoryBudget.FromBytes(1024L * 1024 * 1024))
                .WithTransactionMemoryPool(512L * 1024 * 1024),
            new RuntimeDependencies(failpoints));
        var family = database.ColumnFamilies.DefaultFamily;
        await using var oversized = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        oversized.Put(TestBytes.FromString("first"), new byte[half]);
        oversized.Put(TestBytes.FromString("second"), new byte[half]);
        await using var neighbour = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadWrite);
        neighbour.Put(TestBytes.FromString("neighbour"), TestBytes.FromString("value"));

        var barrier = database.Diagnostics.GetRuntimeMetricsAsync().AsTask();
        await failpoints.WaitForRuntimeBarrierAsync(TimeSpan.FromSeconds(10));
        var oversizedCommit = oversized.CommitAsync(PantsWriteOptions.Sync).AsTask();
        var neighbourCommit = neighbour.CommitAsync(PantsWriteOptions.Sync).AsTask();
        failpoints.ReleaseRuntimeBarrier();
        _ = await barrier.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<PantsResourceLimitException>(() => oversizedCommit);
        await neighbourCommit;
        await using var reader = await database.Transactions.BeginAsync(
            family,
            PantsTransactionMode.ReadOnly);
        Assert.Equal(
            "value",
            TestBytes.ToText((await reader.GetAsync(TestBytes.FromString("neighbour")))!.Value));
    }

    static async Task CommitDeleteAsync(PantsOpenOptions options, byte[] key)
    {
        await using var database = await PantsDatabase.OpenAsync(options);
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadWrite);
        Assert.Throws<PantsResourceLimitException>(() =>
            transaction.Delete(new byte[key.Length + 1]));
        transaction.Delete(key);
        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static byte[] CreateLargestAdmissibleDeleteKey()
    {
        var low = 0;
        var high = DiskFormat.MaximumDecodedBlockBytes;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            try
            {
                EntryAdmission.ValidatePointWrite(middle, null);
                low = middle;
            }
            catch (PantsResourceLimitException)
            {
                high = middle - 1;
            }
        }

        var key = new byte[low];
        key[^1] = 1;
        return key;
    }

    static PantsOpenOptions CreateOptions(string path, bool spilled)
    {
        var options = PantsOpenOptions.Local(path).WithBackgroundCompaction(false);
        return spilled ? options.WithTransactionMemoryPool(1024) : options;
    }
}
