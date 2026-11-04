using Cntryl.Pants.Storage.Internal.Accounting;
using Cntryl.Pants.Support.Failpoints;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage.Accounting;

public sealed class MetadataOriginRoutingTests
{
    [Fact]
    public async Task ShouldAttributeAutomaticWalRecordThresholdFlushToOrdinaryOriginAndRecoverExactRows()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithMemtableLimits(4 * 1024 * 1024)
            .WithFlushAfterWalRecordsForTesting(1);
        var rows = CreateRows(32);
        await using (var database = await PantsDatabase.OpenAsync(options))
        {
            await PutRowsAsync(database, rows);
            await WaitForOnePublishedSstAsync(database);
            await AssertExactRowsAsync(database, rows);

            var snapshot = ((DatabaseInstance)database).MetadataAccounting.Snapshot();
            Assert.Equal(1UL, snapshot.Bucket(MetadataOrigin.OrdinaryLocalFlush, MetadataMedium.Persistent)
                [MetadataCounter.FlushCommittedCount]);
            Assert.Equal(0UL, snapshot.Bucket(MetadataOrigin.Recovery, MetadataMedium.Persistent)
                [MetadataCounter.FlushCommittedCount]);
            await database.ShutdownAsync(TestTimeouts.Expected);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertExactRowsAsync(reopened, rows);
    }

    [Fact]
    public async Task ShouldRetainRecoveryOriginForDeferredRecoveredMemtableFlushAndRecoverExactRows()
    {
        using var directory = new TemporaryDirectory();
        var options = PantsOpenOptions.Local(directory.Path)
            .WithBackgroundCompaction(false)
            .WithMemtableLimits(4 * 1024 * 1024);
        var rows = CreateRows(40 * 1024);
        await using (var seed = await PantsDatabase.OpenAsync(options))
        {
            await PutRowsAsync(seed, rows);
            Assert.Equal(0, (await seed.Diagnostics.GetRuntimeMetricsAsync()).SstCount);
            await seed.ShutdownAsync(TestTimeouts.Expected);
        }

        using var publication = new FlushPipelineFailpointHandler(Failpoint.BeforeFlushPublication);
        await using (var recovered = await PantsDatabase.OpenForTestingAsync(
                         options.WithMemtableLimits(256 * 1024, 64 * 1024),
                         new RuntimeDependencies(publication)))
        {
            try
            {
                await publication.WaitUntilEnteredAsync(TestTimeouts.Expected);
                var pending = await recovered.Diagnostics.GetRuntimeMetricsAsync();
                Assert.Equal(0, pending.SstCount);
                Assert.Equal(1, pending.ImmutableMemtables);
                await AssertExactRowsAsync(recovered, rows);
            }
            finally
            {
                publication.Release();
            }

            await WaitForOnePublishedSstAsync(recovered);
            var snapshot = ((DatabaseInstance)recovered).MetadataAccounting.Snapshot();
            Assert.Equal(1UL, snapshot.Bucket(MetadataOrigin.Recovery, MetadataMedium.Persistent)
                [MetadataCounter.FlushCommittedCount]);
            Assert.Equal(0UL, snapshot.Bucket(MetadataOrigin.OrdinaryLocalFlush, MetadataMedium.Persistent)
                [MetadataCounter.FlushCommittedCount]);
            await recovered.ShutdownAsync(TestTimeouts.Expected);
        }

        await using var reopened = await PantsDatabase.OpenAsync(options);
        await AssertExactRowsAsync(reopened, rows);
    }

    static (byte[] Key, byte[] Value)[] CreateRows(int valueLength) =>
    [
        ("alpha"u8.ToArray(), Enumerable.Repeat((byte)1, valueLength).ToArray()),
        ("bravo"u8.ToArray(), Enumerable.Repeat((byte)2, valueLength).ToArray()),
        ("charlie"u8.ToArray(), Enumerable.Repeat((byte)3, valueLength).ToArray())
    ];

    static async Task PutRowsAsync(IPantsDatabase database, (byte[] Key, byte[] Value)[] rows)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadWrite);
        foreach (var (key, value) in rows)
        {
            transaction.Put(key, value);
        }

        await transaction.CommitAsync(PantsWriteOptions.Sync);
    }

    static Task WaitForOnePublishedSstAsync(IPantsDatabase database) => TestWait.UntilAsync(async () =>
    {
        var metrics = await database.Diagnostics.GetRuntimeMetricsAsync();
        return metrics.SstCount == 1 && metrics.ImmutableMemtables == 0;
    }, "the automatic immutable generation is installed");

    static async Task AssertExactRowsAsync(IPantsDatabase database, (byte[] Key, byte[] Value)[] expected)
    {
        await using var transaction = await database.Transactions.BeginAsync(
            database.ColumnFamilies.DefaultFamily, PantsTransactionMode.ReadOnly);
        await using var scan = await transaction.ScanAsync(new PantsScanQuery());
        var actual = new List<PantsEntry>();
        await foreach (var row in scan)
        {
            actual.Add(row);
        }

        Assert.Equal(expected.Length, actual.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Key, actual[index].Key.ToArray());
            Assert.Equal(expected[index].Value, actual[index].Value.ToArray());
            Assert.Equal(expected[index].Value, (await transaction.GetAsync(expected[index].Key))?.ToArray());
        }
    }
}
