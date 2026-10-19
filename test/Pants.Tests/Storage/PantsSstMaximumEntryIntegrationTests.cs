using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class PantsSstMaximumEntryIntegrationTests
{
    [Fact]
    public async Task ShouldReplayFlushCompactAndReopenLargestWalFramePutEntry()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithMemoryBudget(PantsMemoryBudget.FromBytes(1024L * 1024 * 1024))
            .WithTransactionMemoryPool(512L * 1024 * 1024)
            .WithMemtableLimits(128L * 1024 * 1024);
        var plan = RuntimePlan.Resolve(options);
        var key = new byte[] { 0x6d };
        var sizingMutation = new WalMutation(
            0,
            WalOperation.Put,
            key,
            [],
            1,
            null,
            null);
        var baselineRecord = WalCodec.EncodeTransactionBatch(
            1,
            1,
            1,
            [sizingMutation]);
        var baselineBatchBytes = WalCodec.DecodeRecord(baselineRecord).Value!.Length;
        // The public path's value limit is the smaller of the SST decoded-block ceiling and the
        // complete WAL record ceiling after transaction-batch framing. Exact SST maximum encoding
        // is covered separately by PantsSstEntryAdmissionTests.
        var valueLength = Math.Min(
            DiskFormat.MaximumDecodedBlockBytes - baselineBatchBytes,
            DiskFormat.WalMaximumRecordBytes - baselineRecord.Length);
        var value = new byte[valueLength];
        new Random(0x5eed).NextBytes(value);

        var state = new RuntimeState(
            new ManualClock(DateTimeOffset.UnixEpoch),
            new RuntimeTelemetry());
        using (var store = LocalDiskStore.Open(
                   directory.Path,
                   state,
                   flushWorkingMemoryLimitBytes: plan.FlushWorkingMemoryLimitBytes))
        {
            var family = new ColumnFamilyIdentity(
                0,
                "default",
                RuntimeState.DefaultFamilyVersion);
            var operation = new TransactionIntentOperation(
                0,
                CommitOperationKind.Put,
                family,
                key,
                null,
                value,
                null,
                null,
                false);
            var source = new TransactionOperationSource(
                null,
                [operation],
                1,
                DateTimeOffset.UnixEpoch);
            using var payload = new CommitPayload(
                1,
                PantsTransactionMode.ReadWrite,
                PantsConflictPolicy.LastWriteWins,
                DateTimeOffset.UnixEpoch,
                state.CreateVersion(),
                source,
                []);

            _ = store.AppendCommit(payload, state, PantsDurability.Sync);
        }

        await using (var replayed = await PantsDatabase.OpenAsync(options))
        {
            await AssertValueAsync(replayed, key, value);
            var family = replayed.ColumnFamilies.DefaultFamily;
            await replayed.Maintenance.FlushAsync(family);
            await replayed.Maintenance.CompactAllAsync();
            await AssertValueAsync(replayed, key, value);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertValueAsync(reopened, key, value);
        var report = await reopened.PersistentStorage!.VerifyAsync(TestTimeouts.Expected);
        Assert.True(report.Authoritative);
        Assert.True(report.SstFilesVerified > 0);
    }

    static async Task AssertValueAsync(IPantsDatabase database, byte[] key, byte[] expected)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily,
            PantsTransactionMode.ReadOnly);
        var actual = await transaction.GetAsync(key);

        Assert.True(actual.HasValue);
        Assert.True(actual.Value.Span.SequenceEqual(expected));
    }
}
