using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class CompactionRepairScratchTests
{
    [Fact]
    public void ShouldBoundRepairRunsAndPreserveRawHistoryThroughMultiplePasses()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, ".flush-staging"));
        var inputs = Enumerable.Range(1, 17).Select(index => WriteInput(directory.Path, index,
        [
            new SstEntry("key"u8.ToArray(), index == 17 ? null : TestBytes.FromString($"v{index}"),
                checked((ulong)index), checked((ulong)index + 1000), index == 17)
        ], [new RangeTombstone("r"u8.ToArray(), "t"u8.ToArray(), checked((ulong)index))])).ToArray();
        var plan = Plan(inputs) with { SnapshotHorizon = 0 };
        var budget = new ResourceBudget(256 * 1024);
        var readers = new ReaderCounter();
        using (var scratch = new CompactionRepairScratch(
                   directory.Path, 8 * 1024 * 1024, PantsPerformanceGoal.Latency, readers.Open))
        using (var streams = scratch.Reduce(plan, Open, 2, 256, budget, CancellationToken.None))
        {
            Assert.InRange(streams.Streams.Count, 1, 2);
            var partitions = StreamingCompactionMerger.MergeAndPartition(
                streams.Streams, streams.RangeTombstones, plan, 256, budget).ToArray();
            var entries = partitions.SelectMany(static partition => partition.Entries).ToArray();
            Assert.Equal(Enumerable.Range(1, 17).Reverse().Select(static value => checked((ulong)value)),
                entries.Select(static entry => entry.Sequence));
            Assert.True(entries[0].IsDelete);
            Assert.All(entries, static entry => Assert.Equal(entry.Sequence + 1000, entry.Expiration));
            var ranges = partitions.SelectMany(static partition => partition.RangeTombstones).ToArray();
            Assert.Equal(Enumerable.Range(1, 17).Reverse().Select(static value => checked((ulong)value)),
                ranges.Select(static range => range.Sequence));
        }

        Assert.Equal(0, budget.Current);
        Assert.InRange(budget.Peak, 1, budget.Limit);
        Assert.Equal(0, readers.OpenCount);
        Assert.InRange(readers.PeakOpenCount, 1, 2);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory.Path, ".flush-staging")));

        ICompactionFileCursor Open(FileMeta file) => new LocalFileCursor(
            readers.Open(Path.Combine(directory.Path, file.Name)), budget);
    }

    [Fact]
    public void ShouldRemovePartialScratchAndPreserveInputsWhenCapacityIsExhausted()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, ".flush-staging"));
        var inputs = Enumerable.Range(1, 3).Select(index => WriteInput(directory.Path, index,
            [new SstEntry("key"u8.ToArray(), "value"u8.ToArray(), checked((ulong)index), null, false)], [])).ToArray();
        var before = inputs.Select(file => File.ReadAllBytes(Path.Combine(directory.Path, file.Name))).ToArray();
        var budget = new ResourceBudget(256 * 1024);
        using (var scratch = new CompactionRepairScratch(directory.Path, 64, PantsPerformanceGoal.Latency))
        {
            Assert.Throws<PantsResourceLimitException>(() =>
                scratch.Reduce(Plan(inputs), Open, 2, 256, budget, CancellationToken.None));
            Assert.All(Directory.EnumerateFiles(Path.Combine(directory.Path, ".flush-staging")),
                static path => Assert.InRange(new FileInfo(path).Length, 0, 32));
        }

        for (var index = 0; index < inputs.Length; index++)
        {
            Assert.Equal(before[index], File.ReadAllBytes(Path.Combine(directory.Path, inputs[index].Name)));
        }

        Assert.Equal(0, budget.Current);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory.Path, ".flush-staging")));

        ICompactionFileCursor Open(FileMeta file) => new LocalFileCursor(
            SstReader.Open(Path.Combine(directory.Path, file.Name)), budget);
    }

    [Fact]
    public void ShouldRemoveEarlierScratchWhenALaterBatchContainsConflictingVersions()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, ".flush-staging"));
        var inputs = Enumerable.Range(1, 4).Select(index => WriteInput(directory.Path, index,
            [new SstEntry("key"u8.ToArray(), TestBytes.FromString($"v{index}"),
                checked((ulong)Math.Min(index, 3)), null, false)], [])).ToArray();
        var budget = new ResourceBudget(256 * 1024);
        using (var scratch = new CompactionRepairScratch(directory.Path, 1024 * 1024, PantsPerformanceGoal.Latency))
        {
            Assert.Throws<PantsCorruptionException>(() =>
                scratch.Reduce(Plan(inputs), Open, 2, 256, budget, CancellationToken.None));
            Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(directory.Path, ".flush-staging")));
        }

        Assert.Equal(0, budget.Current);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory.Path, ".flush-staging")));
        Assert.All(inputs, file => Assert.True(File.Exists(Path.Combine(directory.Path, file.Name))));

        ICompactionFileCursor Open(FileMeta file) => new LocalFileCursor(
            SstReader.Open(Path.Combine(directory.Path, file.Name)), budget);
    }

    static CompactionPlan Plan(FileMeta[] inputs) => new(1, 1, 0, null, false, false, inputs, CompactionPurpose.Repair);

    static FileMeta WriteInput(string directory, int index, SstEntry[] entries, RangeTombstone[] ranges)
    {
        var name = $"input-{index}.sst";
        var bytes = SstCodec.Encode(entries, ranges, PantsPerformanceGoal.Latency);
        File.WriteAllBytes(Path.Combine(directory, name), bytes);
        var (smallest, largest) = SstCodec.GetKeyBounds(entries, ranges);
        return new FileMeta
        {
            Name = name,
            Level = 1,
            SmallestKey = smallest,
            LargestKey = largest,
            SizeBytes = checked((ulong)bytes.Length),
            KeyBoundsComplete = true
        };
    }

    sealed class ReaderCounter
    {
        public int OpenCount { get; private set; }

        public int PeakOpenCount { get; private set; }

        public SstReader Open(string path)
        {
            OpenCount++;
            PeakOpenCount = Math.Max(PeakOpenCount, OpenCount);
            return SstReader.Open(path, new Reservation(this));
        }

        sealed class Reservation(ReaderCounter owner) : IDisposable
        {
            bool _released;

            public void Dispose()
            {
                if (!_released)
                {
                    owner.OpenCount--;
                    _released = true;
                }
            }
        }
    }
}
