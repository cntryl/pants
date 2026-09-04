using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class CompactionVersionConflictTests
{
    public static TheoryData<bool, string, bool> ConflictingVersions => new()
    {
        { false, "value", false }, { true, "value", false },
        { false, "deletion", false }, { true, "deletion", false },
        { false, "expiration", false }, { true, "expiration", false },
        { false, "value", true }, { true, "value", true },
        { false, "deletion", true }, { true, "deletion", true },
        { false, "expiration", true }, { true, "expiration", true }
    };

    [Theory]
    [MemberData(nameof(ConflictingVersions))]
    public void ShouldRejectConflictingLogicalVersionsEvenWhenANewerValueHidesThem(
        bool streaming, string difference, bool hidden)
    {
        var first = Entry("first", 7);
        var second = difference switch
        {
            "value" => Entry("second", 7),
            "deletion" => first with { Value = null, IsDelete = true },
            "expiration" => first with { Expiration = ulong.MaxValue },
            _ => throw new ArgumentOutOfRangeException(nameof(difference))
        };
        SstEntry[] left = hidden ? [Entry("newest", 9), first] : [first];

        Assert.Throws<PantsCorruptionException>(() => Merge(streaming, left, [second]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldRejectConflictingVersionsBeforeDroppingACoveredRange(bool streaming)
    {
        RangeTombstone[] ranges = [new("a"u8.ToArray(), "z"u8.ToArray(), 10)];

        Assert.Throws<PantsCorruptionException>(() =>
            Merge(streaming, [Entry("first", 7)], [Entry("second", 7)], ranges));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldRetainOneIdenticalVersionAboveTheSnapshotHorizon(bool streaming)
    {
        var result = Merge(streaming, [Entry("same", 7)], [Entry("same", 7)], horizon: 5);

        var entry = Assert.Single(result);
        Assert.Equal("same"u8.ToArray(), entry.Value);
        Assert.Equal(7UL, entry.Sequence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShouldRejectConflictingVersionsWithinOneInput(bool streaming)
    {
        Assert.Throws<PantsCorruptionException>(() =>
            Merge(streaming, [Entry("first", 7), Entry("second", 7)], []));
    }

    static SstEntry[] Merge(
        bool streaming,
        SstEntry[] left,
        SstEntry[] right,
        RangeTombstone[]? ranges = null,
        long? horizon = null)
    {
        var plan = new CompactionPlan(0, 1, 0, horizon, true, true, []);
        if (!streaming)
        {
            return CompactionMerger.Merge(
                [new SstContents(left, ranges ?? [], 1), new SstContents(right, [], 1)], plan)
                .Entries.ToArray();
        }

        using var directory = new TemporaryDirectory();
        var leftPath = Path.Combine(directory.Path, "left.sst");
        var rightPath = Path.Combine(directory.Path, "right.sst");
        File.WriteAllBytes(leftPath, SstCodec.Encode(left, ranges ?? [], PantsPerformanceGoal.Latency));
        File.WriteAllBytes(rightPath, SstCodec.Encode(right, [], PantsPerformanceGoal.Latency));
        using var leftReader = SstReader.Open(leftPath);
        using var rightReader = SstReader.Open(rightPath);
        var budget = new ResourceBudget(1024 * 1024);
        try
        {
            return StreamingCompactionMerger.MergeAndPartition(
                    [leftReader, rightReader], plan, 1024, budget)
                .SelectMany(static partition => partition.Entries).ToArray();
        }
        finally
        {
            Assert.Equal(0, budget.Current);
        }
    }

    static SstEntry Entry(string value, ulong sequence) =>
        new("key"u8.ToArray(), System.Text.Encoding.UTF8.GetBytes(value), sequence, null, false);
}
