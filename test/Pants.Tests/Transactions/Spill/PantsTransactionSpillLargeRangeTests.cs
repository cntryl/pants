using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Transactions.Spill;

public sealed class PantsTransactionSpillLargeRangeTests
{
    const int RangeEndBytes = 40 * 1_024 * 1_024;
    const int TriggerValueBytes = 16 * 1_024 * 1_024;

    /// <summary>
    ///     A range node once carried its own end and its subtree's maximum end inline, so a
    ///     single admissible range with a ~40 MiB end produced an ~80 MiB node frame and the
    ///     next spill failed against the 64 MiB frame limit.
    /// </summary>
    [Fact]
    public async Task ShouldSpillCommitAndReopenGivenAdmissibleRangeWithLargeEnd()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(512L * 1_024 * 1_024))
            .WithTransactionMemoryPool(48L * 1_024 * 1_024)
            .WithBackgroundCompaction(false);
        var rangeEnd = CreateRangeEnd();
        var triggerValue = new byte[TriggerValueBytes];
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await using (var setup = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                setup.Put("k1"u8.ToArray(), "committed-before"u8.ToArray());
                setup.Put("a"u8.ToArray(), "outside-range"u8.ToArray());
                await setup.CommitAsync(PantsWriteOptions.Buffered);
            }

            await using (var transaction = await database.Transactions.BeginAsync(
                             database.ColumnFamilies.DefaultFamily,
                             PantsTransactionMode.ReadWrite))
            {
                transaction.DeleteRange("k"u8.ToArray(), rangeEnd);
                transaction.Put("k2"u8.ToArray(), "after-range"u8.ToArray());
                transaction.Put("z"u8.ToArray(), triggerValue);

                Assert.NotEmpty(TransactionSpillTestHarness.FindArtifacts(directory.Path));
                await AssertVisibleStateAsync(transaction);

                await transaction.CommitAsync(PantsWriteOptions.Buffered);
            }

            await AssertCommittedStateAsync(database);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertCommittedStateAsync(reopened);
    }

    /// <summary>
    ///     A subtree maximum can belong to a different range than the node carrying it, so two
    ///     individually admissible ranges once combined into one oversized node frame.
    /// </summary>
    [Fact]
    public void ShouldSpillAndLookUpRangesGivenSubtreeMaximumFromAnotherLargeRange()
    {
        using var directory = new TemporaryDirectory();
        var family = new ColumnFamilyIdentity(0, "default", 0);
        using var store = new TransactionSpillStore(directory.Path, 1, family);
        var outer = CreateRangeEnd((byte)'z', RangeEndBytes);
        var inner = CreateRangeEnd((byte)'c', 30 * 1_024 * 1_024);
        TransactionIntentOperation[] operations =
        [
            new(0, CommitOperationKind.DeleteRange, family, "a"u8.ToArray(), outer, null, null, null, false),
            new(1, CommitOperationKind.DeleteRange, family, "b"u8.ToArray(), inner, null, null, null, false)
        ];

        store.WriteRun(operations);

        var visited = new List<ulong>();
        store.ForEach(operation => visited.Add(operation.Ordinal));
        Assert.Equal([0UL, 1UL], visited);
        Assert.Equal(1UL, store.LatestBefore(ulong.MaxValue, "b5"u8)!.Ordinal);
        Assert.Equal(0UL, store.LatestBefore(ulong.MaxValue, "y"u8)!.Ordinal);
        Assert.Equal(0UL, store.LatestBefore(1, "b5"u8)!.Ordinal);
        Assert.Null(store.LatestBefore(ulong.MaxValue, "{"u8));
    }

    static byte[] CreateRangeEnd() => CreateRangeEnd((byte)'k', RangeEndBytes);

    static byte[] CreateRangeEnd(byte prefix, int length)
    {
        var end = new byte[length];
        end.AsSpan().Fill(0xFF);
        end[0] = prefix;
        return end;
    }

    static async Task AssertCommittedStateAsync(IPantsDatabase database)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        await AssertVisibleStateAsync(transaction);
    }

    static async Task AssertVisibleStateAsync(IPantsTransaction transaction)
    {
        Assert.Null(await transaction.GetAsync("k1"u8.ToArray()));
        Assert.Equal("after-range", TestBytes.ToText((await transaction.GetAsync("k2"u8.ToArray()))!.Value));
        Assert.Equal(TriggerValueBytes, (await transaction.GetAsync("z"u8.ToArray()))!.Value.Length);

        await using var scan = await transaction.ScanAsync(new PantsScanQuery
        {
            StartInclusive = "a"u8.ToArray(),
            EndExclusive = "y"u8.ToArray()
        });
        var keys = new List<string>();
        await foreach (var row in scan)
        {
            keys.Add(TestBytes.ToText(row.Key));
        }

        Assert.Equal(["a", "k2"], keys);
    }
}
