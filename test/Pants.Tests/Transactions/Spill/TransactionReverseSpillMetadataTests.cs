using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Transactions.Spill;

public sealed class TransactionReverseSpillMetadataTests
{
    [Theory]
    [InlineData(80, false)]
    [InlineData(4096, true)]
    public void ShouldReverseScanTheSameSpilledRowsWithAdmittedOrStreamingChunkMetadata(long capacity, bool materialized)
    {
        using var directory = new TemporaryDirectory();
        using var store = new TransactionSpillStore(directory.Path, 1, new ColumnFamilyIdentity(0, "default", 0));
        store.WriteRun(Enumerable.Range(0, 256).Select(index => new TransactionIntentOperation(
            checked((ulong)index), CommitOperationKind.Put, new ColumnFamilyIdentity(0, "default", 0),
            TestBytes.FromString($"key-{index:0000}"), null, [1], null, null, false)).ToArray());
        var pool = new TransactionMemoryPool(capacity);
        using (var view = store.CreateReadView([], pool))
        {
            var baseline = pool.Used;
            Assert.True(baseline > 0);
            using (var scan = view.CreateKeyScan(null, null, PantsScanDirection.Reverse))
            {
                Assert.Equal(materialized, pool.Used > baseline);
                var actual = new List<string>();
                while (scan.MoveNext())
                {
                    actual.Add(TestBytes.ToText(scan.Current));
                }
                Assert.Equal(Enumerable.Range(0, 256).Reverse().Select(index => $"key-{index:0000}"), actual);
                Assert.InRange(pool.Used, baseline, capacity);
            }
            Assert.Equal(baseline, pool.Used);
        }
        Assert.Equal(0, pool.Used);
    }
}
