using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

public sealed class CompactionStreamsTests
{
    [Fact]
    public void ShouldKeepAtMostSourceStreamsPlusOneReadersOpenAcrossSixtyFiveTargets()
    {
        using var directory = new TemporaryDirectory();
        var tracker = new ReaderTracker();
        var targets = Enumerable.Range(0, 65)
            .Select(index => WriteFile(
                directory.Path,
                $"target-{index:D3}.sst",
                1,
                (ulong)(index + 1),
                [($"k{index:D3}-a", 1UL, "t"), ($"k{index:D3}-b", 1UL, "t")]))
            .ToArray();
        var source = WriteFile(
            directory.Path,
            "source.sst",
            0,
            100,
            [("k010-a", 50UL, "new"), ("k064-a", 51UL, "new")]);
        var plan = Plan(0, 1, [source, .. targets]);

        using var streams = CompactionStreams.Create(
            plan,
            file => Path.Combine(directory.Path, file.Name),
            tracker.Open,
            4,
            null);
        var peak = 0;
        var merged = new List<CompactionMergeResult>();
        foreach (var partition in StreamingCompactionMerger.MergeAndPartition(
                     streams.Streams,
                     streams.RangeTombstones,
                     plan,
                     1024 * 1024))
        {
            peak = Math.Max(peak, tracker.OpenCount);
            merged.Add(partition);
        }

        // One level-0 source stream plus one chained target stream.
        Assert.True(peak <= 2, $"{peak} readers were open at once.");
        var keys = merged.SelectMany(static partition => partition.Entries)
            .Select(static entry => TestBytes.ToText(entry.Key))
            .ToArray();
        Assert.Equal(130, keys.Length);
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        var newest = merged.SelectMany(static partition => partition.Entries)
            .Single(static entry => TestBytes.ToText(entry.Key) == "k010-a");
        Assert.Equal(50UL, newest.Sequence);
        Assert.Equal(0, tracker.OpenCount);
    }

    [Fact]
    public void ShouldPlanTenThousandTargetFilesWithOneSourceStream()
    {
        var files = new List<FileMeta>
        {
            Meta("source.sst", 0, 1, "a", "z")
        };
        files.AddRange(Enumerable.Range(0, 10_000).Select(index =>
            Meta($"target-{index:D5}.sst", 1, (ulong)(index + 10), $"b{index:D5}", $"b{index:D5}")));

        var plan = LeveledCompactionPlanner.Pick(
            files,
            0,
            new PantsCompactionConfiguration(L0FileCountTrigger: 1, MaximumInputFiles: 64),
            null,
            true);

        Assert.NotNull(plan);
        Assert.Equal(1, plan.Inputs.Count(static file => file.Level == 0));
        Assert.Equal(10_000, plan.Inputs.Count(static file => file.Level == 1));
    }

    [Fact]
    public void ShouldFailWithResourceLimitWhenSourceStreamsExceedTheCap()
    {
        using var directory = new TemporaryDirectory();
        var sources = Enumerable.Range(0, 3)
            .Select(index => WriteFile(
                directory.Path,
                $"source-{index}.sst",
                0,
                (ulong)(index + 1),
                [($"k{index}", 1UL, "v")]))
            .ToArray();

        var exception = Assert.Throws<PantsResourceLimitException>(() => CompactionStreams.Create(
            Plan(0, 1, sources),
            file => Path.Combine(directory.Path, file.Name),
            SstReader.Open,
            2,
            null));

        Assert.Contains("simultaneous source streams", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldFailWithResourceLimitWhenRangeTombstonesExceedTheBudget()
    {
        using var directory = new TemporaryDirectory();
        var tombstones = Enumerable.Range(0, 200)
            .Select(index => ($"a{index:D4}", $"a{index:D4}z", (ulong)(index + 1)))
            .ToArray();
        var file = WriteFile(directory.Path, "source.sst", 0, 1, [("k", 1UL, "v")], tombstones);

        Assert.Throws<PantsResourceLimitException>(() => CompactionStreams.Create(
            Plan(0, 1, [file]),
            f => Path.Combine(directory.Path, f.Name),
            SstReader.Open,
            4,
            new ResourceBudget(1024)));
    }

    [Fact]
    public void ShouldMergeEqualKeyHistoryAcrossTargetFilesSharingABoundaryKey()
    {
        using var directory = new TemporaryDirectory();
        var left = WriteFile(
            directory.Path,
            "left.sst",
            1,
            1,
            [("a", 3UL, "a3"), ("m", 9UL, "m9")]);
        var right = WriteFile(
            directory.Path,
            "right.sst",
            1,
            2,
            [("m", 5UL, "m5"), ("z", 4UL, "z4")]);
        var source = WriteFile(directory.Path, "source.sst", 0, 3, [("m", 20UL, "m20")]);
        var plan = Plan(0, 1, [source, left, right]);

        using var streams = CompactionStreams.Create(
            plan,
            file => Path.Combine(directory.Path, file.Name),
            SstReader.Open,
            4,
            null);
        var entries = StreamingCompactionMerger.MergeAndPartition(
                streams.Streams,
                streams.RangeTombstones,
                plan,
                1024 * 1024)
            .SelectMany(static partition => partition.Entries)
            .Select(static entry => (TestBytes.ToText(entry.Key), entry.Sequence))
            .ToArray();

        Assert.Equal([("a", 3UL), ("m", 20UL), ("z", 4UL)], entries);
    }

    static CompactionPlan Plan(uint sourceLevel, uint targetLevel, IReadOnlyList<FileMeta> inputs) =>
        new(sourceLevel, targetLevel, 0, null, true, true, inputs);

    static FileMeta Meta(string name, uint level, ulong sequence, string smallest, string largest) => new()
    {
        Name = name,
        Level = level,
        SstSequence = sequence,
        SizeBytes = 1024,
        SmallestKey = [.. System.Text.Encoding.UTF8.GetBytes(smallest).Select(static value => (int)value)],
        LargestKey = [.. System.Text.Encoding.UTF8.GetBytes(largest).Select(static value => (int)value)],
        KeyBoundsComplete = true
    };

    static FileMeta WriteFile(
        string directory,
        string name,
        uint level,
        ulong sequence,
        (string Key, ulong Sequence, string Value)[] entries,
        (string Start, string End, ulong Sequence)[]? tombstones = null)
    {
        var sstEntries = entries
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ThenByDescending(static entry => entry.Sequence)
            .Select(static entry => new SstEntry(
                TestBytes.FromString(entry.Key),
                TestBytes.FromString(entry.Value),
                entry.Sequence,
                null,
                false))
            .ToArray();
        var ranges = (tombstones ?? [])
            .Select(static range => new RangeTombstone(
                TestBytes.FromString(range.Start),
                TestBytes.FromString(range.End),
                range.Sequence))
            .ToArray();
        File.WriteAllBytes(
            Path.Combine(directory, name),
            SstCodec.Encode(sstEntries, ranges, PantsPerformanceGoal.Latency));
        return Meta(name, level, sequence, sstEntries.Select(static entry => TestBytes.ToText(entry.Key)).Order(StringComparer.Ordinal).First(), sstEntries.Select(static entry => TestBytes.ToText(entry.Key)).Order(StringComparer.Ordinal).Last());
    }

    sealed class ReaderTracker
    {
        readonly List<SstReader> _readers = [];

        public int OpenCount
        {
            get
            {
                lock (_readers)
                {
                    return _readers.Count(static reader => !reader.IsDisposed);
                }
            }
        }

        public SstReader Open(string path)
        {
            var reader = SstReader.Open(path);
            lock (_readers)
            {
                _readers.Add(reader);
            }

            return reader;
        }
    }
}
