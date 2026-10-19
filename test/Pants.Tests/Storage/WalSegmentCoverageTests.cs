using Cntryl.Pants.Storage.Internal.Wal;
using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class WalSegmentCoverageTests
{
    [Fact]
    public void ShouldPruneOnlyTheCoveredOldestPrefixWhenWriterEpochsAreMonotonic()
    {
        var prunable = WalSegmentCoverage.SelectPrunable(
        [
            new WalSegmentCoverageResult(false, 1, 1),
            new WalSegmentCoverageResult(true, 1, 2),
            new WalSegmentCoverageResult(true, 2, 2)
        ]);

        Assert.Equal([false, false, false], prunable);
    }

    [Fact]
    public void ShouldRetainCoveredSegmentWhenLaterRetainedSegmentHoldsLowerEpoch()
    {
        var prunable = WalSegmentCoverage.SelectPrunable(
        [
            new WalSegmentCoverageResult(true, 2, 2),
            new WalSegmentCoverageResult(false, 1, 1)
        ]);

        Assert.Equal([false, false], prunable);
    }

    [Fact]
    public void ShouldPruneCoveredPrefixAndRetainEveryLaterSegmentAfterFirstGap()
    {
        var prunable = WalSegmentCoverage.SelectPrunable(
        [
            new WalSegmentCoverageResult(true, 1, 1),
            new WalSegmentCoverageResult(true, 1, 2),
            new WalSegmentCoverageResult(false, 2, 2),
            new WalSegmentCoverageResult(true, 2, 2)
        ]);

        Assert.Equal([true, true, false, false], prunable);
    }

    [Fact]
    public void ShouldRetainInterleavedTransactionWhoseCommitIsBeyondTheFamilyFrontier()
    {
        using var directory = new TemporaryDirectory();
        var records = new byte[][]
        {
            WalCodec.EncodeTransactionMarker(WalOperation.TransactionBegin, 7, 1, 1),
            WalCodec.EncodeTransactionMutation(
                new WalMutation(0, WalOperation.Put, "late"u8.ToArray(), "value"u8.ToArray(), 2, null, null),
                7,
                1),
            WalCodec.EncodeTransactionBatch(
                8,
                3,
                1,
                [new WalMutation(0, WalOperation.Put, "covered"u8.ToArray(), "value"u8.ToArray(), 3, null, null)]),
            WalCodec.EncodeTransactionMarker(WalOperation.TransactionCommit, 7, 5, 1)
        };

        var coverage = WalSegmentCoverage.Evaluate(
            Frame(records),
            new Dictionary<uint, ulong> { [0] = 4 },
            new HashSet<uint> { 0 },
            directory.Path);

        Assert.False(coverage.Covered);
    }

    static byte[] Frame(IEnumerable<byte[]> records)
    {
        using var stream = new MemoryStream();
        foreach (var record in records)
        {
            Cntryl.Pants.Storage.Internal.DiskFormat.WriteUInt32(stream, checked((uint)record.Length));
            Cntryl.Pants.Storage.Internal.DiskFormat.WriteUInt32(
                stream,
                Cntryl.Pants.Storage.Internal.DiskFormat.Crc32C(record));
            stream.Write(record);
        }

        return stream.ToArray();
    }
}
