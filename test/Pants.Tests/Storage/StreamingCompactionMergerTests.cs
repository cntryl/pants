using Cntryl.Pants.Support.TestDoubles;

namespace Cntryl.Pants.Storage;

/// <summary>
///     Slice 5 (issue #219), the merge/partition side: <see cref="StreamingCompactionMerger" /> does
///     an incremental k-way merge over per-input <see cref="SstBlockIterator" />s and emits completed
///     output partitions as it goes, instead of <c>CompactionMerger.Merge</c> materializing every
///     input's entries into one array before <c>CompactionOutputPartitioner.Partition</c> slices it.
///     Every test here is an oracle-equivalence check against that existing (unchanged, still used
///     elsewhere) pipeline, over the same input <see cref="SstContents" /> — same entries, same
///     range-tombstone masking/retention/GC-eligibility rules, same partition boundaries — since
///     that logic is durability-critical and was deliberately not reimplemented, only re-driven
///     incrementally.
/// </summary>
public sealed class StreamingCompactionMergerTests
{
    [Fact]
    public void ShouldMatchTheExistingMergerGivenNonOverlappingInputs()
    {
        AssertEquivalent(
            [Build(("a", 1, "1"), ("b", 2, "2")), Build(("c", 3, "3"), ("d", 4, "4"))],
            Plan(),
            1024 * 1024);
    }

    [Fact]
    public void ShouldPreferTheNewestVersionGivenTheSameKeyInMultipleInputs()
    {
        AssertEquivalent(
            [Build(("key", 1, "old")), Build(("key", 5, "new"))],
            Plan(),
            1024 * 1024);
    }

    [Fact]
    public void ShouldDropAPointTombstoneOnlyOnceItIsGcEligibleBelowTheHorizon()
    {
        var files = new[] { Build(("key", 1, null), ("other", 2, "value")) };
        AssertEquivalent(files, Plan(10), 1024 * 1024);
        AssertEquivalent(files, Plan(0), 1024 * 1024);
        AssertEquivalent(files, Plan(10, false), 1024 * 1024);
    }

    [Fact]
    public void ShouldRetainOnlyOneOlderVersionAtOrBelowTheSnapshotHorizon()
    {
        // Same key across three separate input files, mirroring overlapping generations.
        var files = new[] { Build(("key", 1, "v1")), Build(("key", 5, "v5")), Build(("key", 9, "v9")) };
        AssertEquivalent(files, Plan(5), 1024 * 1024);
    }

    [Fact]
    public void ShouldMaskEntriesCoveredByAGcEligibleRangeTombstone()
    {
        var file = Build([("a", 1, "a"), ("b", 2, "b"), ("c", 3, "c")], [("a", "c", 10)]);
        AssertEquivalent([file], Plan(20, rangeTombstoneGcEligible: true), 1024 * 1024);
        AssertEquivalent([file], Plan(20, rangeTombstoneGcEligible: false), 1024 * 1024);
    }

    [Fact]
    public void ShouldProduceMultipleOutputPartitionsGivenATightSizeBudgetAndClampTombstonesToEach()
    {
        var entries = Enumerable.Range(0, 40)
            .Select(index => ($"key-{index:D4}", (ulong)(index + 1), (string?)new string('v', 200)))
            .ToArray();
        var file = Build(entries, [("key-0005", "key-0035", 100)]);

        AssertEquivalent([file], Plan(), 2048);
    }

    [Fact]
    public void ShouldMergeManySmallInputsAcrossMultipleOverlappingKeyRanges()
    {
        var files = Enumerable.Range(0, 6)
            .Select(fileIndex => Build(Enumerable.Range(0, 20)
                .Select(index => (
                    $"key-{index:D4}",
                    (ulong)(fileIndex * 100 + index),
                    (string?)$"f{fileIndex}-v{index}"))
                .ToArray()))
            .ToArray();

        AssertEquivalent(files, Plan(), 4096);
    }

    [Fact]
    public void ShouldDrainEveryVersionGivenDuplicateKeysWithinOneInput()
    {
        var file = Build(
            ("key", 9, null),
            ("key", 5, "old"),
            ("key", 1, "older"));

        AssertEquivalent(
            [file],
            Plan(10),
            1024 * 1024);
    }

    [Fact]
    public void ShouldMatchTheExistingMergerWhenDuplicateVersionsSpanManyBlocks()
    {
        var versions = Enumerable.Range(1, 48)
            .Select(index => (
                "large-key",
                checked((ulong)index),
                (string?)new string((char)('a' + index % 26), 4096)))
            .Reverse()
            .ToArray();

        AssertEquivalent(
            [Build(versions)],
            Plan(24),
            1024 * 1024);
    }

    [Fact]
    public void ShouldPreserveRawSourceHistoryAndSplitRangeTombstoneAtFragmentBoundary()
    {
        var source = Build(
            [("a", 8, "new"), ("a", 3, "old"), ("b", 6, null), ("b", 2, "masked"), ("z", 9, "right")],
            [("b", "y", 7)]);
        using var directory = new TemporaryDirectory();
        WriteSst(directory.Path, "source.sst", source);
        var plan = FragmentPlan("a", "z", "m");
        using var streams = CompactionStreams.Create(
            plan,
            file => Path.Combine(directory.Path, file.Name),
            SstReader.Open,
            4,
            null);

        var partitions = StreamingCompactionMerger.MergeAndPartition(
                streams.Streams,
                streams.RangeTombstones,
                plan,
                1024 * 1024)
            .ToArray();

        Assert.Equal(2, partitions.Length);
        Assert.Equal(
            source.Entries.Select(entry => (
                Key: TestBytes.ToText(entry.Key),
                entry.Sequence,
                Value: entry.Value is null ? null : TestBytes.ToText(entry.Value),
                entry.IsDelete)),
            partitions.SelectMany(static partition => partition.Entries).Select(entry => (
                Key: TestBytes.ToText(entry.Key),
                entry.Sequence,
                Value: entry.Value is null ? null : TestBytes.ToText(entry.Value),
                entry.IsDelete)));
        Assert.Equal(
            [(Start: "b", End: "m", Sequence: 7UL)],
            partitions[0].RangeTombstones.Select(range => (
                Start: TestBytes.ToText(range.Start),
                End: TestBytes.ToText(range.End),
                range.Sequence)));
        Assert.Equal(
            [(Start: "m", End: "y", Sequence: 7UL)],
            partitions[1].RangeTombstones.Select(range => (
                Start: TestBytes.ToText(range.Start),
                End: TestBytes.ToText(range.End),
                range.Sequence)));
    }

    [Fact]
    public void ShouldRejectShadowedConflictingVersionsWhileFragmentingRawHistory()
    {
        var source = Build(
            [("a", 1, "left"), ("z", 9, "new"), ("z", 3, "old-one"), ("z", 3, "old-two")],
            []);
        using var directory = new TemporaryDirectory();
        using var reader = OpenReader(directory.Path, "source.sst", source);
        var plan = FragmentPlan("a", "z", "m");

        var exception = Assert.Throws<PantsCorruptionException>(() =>
            StreamingCompactionMerger.MergeAndPartition([reader], plan, 1024 * 1024).ToArray());

        Assert.Equal(PantsErrorCode.Corruption, exception.Code);
    }

    [Fact]
    public void ShouldRejectFragmentPlansWithInvalidShape()
    {
        var valid = FragmentPlan("a", "z", "m");
        CompactionPlan[] invalidPlans =
        [
            valid with { SourceLevel = 0 },
            valid with { TargetLevel = 3 },
            valid with { Inputs = [] },
            valid with { FragmentSplitKey = [] },
            valid with { PointTombstoneGcEligible = true },
            valid with { RangeTombstoneGcEligible = true },
            valid with
            {
                Inputs =
                [
                    .. valid.Inputs,
                    new FileMeta
                    {
                        Name = "other.sst",
                        Level = 2,
                        SmallestKey = TestBytes.FromString("a"),
                        LargestKey = TestBytes.FromString("z"),
                        KeyBoundsComplete = true
                    }
                ]
            }
        ];

        foreach (var plan in invalidPlans)
        {
            var stream = new TrackingEntryStream();
            var exception = Assert.Throws<PantsCorruptionException>(() =>
                StreamingCompactionMerger.MergeAndPartition([stream], [], plan, 1024).ToArray());

            Assert.Equal(PantsErrorCode.Corruption, exception.Code);
            Assert.True(stream.IsDisposed);
        }
    }

    [Fact]
    public void ShouldRejectFragmentBoundariesThatAreNotStrictlyInterior()
    {
        foreach (var boundary in new[] { "", "a", "z", "zz" })
        {
            var stream = new TrackingEntryStream();
            var plan = FragmentPlan("a", "z", boundary);

            var exception = Assert.Throws<PantsCorruptionException>(() =>
                StreamingCompactionMerger.MergeAndPartition([stream], [], plan, 1024).ToArray());

            Assert.Equal(PantsErrorCode.Corruption, exception.Code);
            Assert.True(stream.IsDisposed);
        }
    }

    [Fact]
    public void ShouldKeepFragmentOrdinalsContiguousWhenSoftRollPrecedesTheForcedCut()
    {
        var source = Build([("z", 2, "right")], [("a", "b", 1)]);
        using var directory = new TemporaryDirectory();
        using var reader = OpenReader(directory.Path, "source.sst", source);
        var plan = FragmentPlan("a", "z", "m");

        var partitions = StreamingCompactionMerger.MergeAndPartition([reader], plan, 1).ToArray();

        Assert.Equal(2, partitions.Length);
        Assert.Empty(partitions[0].Entries);
        Assert.Equal(["z"], partitions[1].Entries.Select(static entry => TestBytes.ToText(entry.Key)));
        Assert.Equal(("a", "b"), (
            TestBytes.ToText(Assert.Single(partitions[0].RangeTombstones).Start),
            TestBytes.ToText(partitions[0].RangeTombstones[0].End)));
    }

    [Fact]
    public void ShouldDisposeFragmentStreamWhenRawKeyGroupExceedsMemoryBudget()
    {
        var entries = Enumerable.Range(0, 8)
            .Select(index => new SstEntry(
                TestBytes.FromString("a"),
                new byte[100],
                checked((ulong)(20 - index)),
                null,
                false))
            .ToArray();
        var stream = new TrackingEntryStream(entries);
        var plan = FragmentPlan("a", "z", "m");
        var budget = new ResourceBudget(512);

        var exception = Assert.Throws<PantsResourceLimitException>(() =>
            StreamingCompactionMerger.MergeAndPartition(
                    [stream],
                    [],
                    plan,
                    1,
                    budget)
                .ToArray());

        Assert.Equal(PantsErrorCode.ResourceLimit, exception.Code);
        Assert.True(stream.IsDisposed);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public void ShouldFragmentRangeOnlySourceWhenInteriorBoundaryHasNoPointEntry()
    {
        var source = Build([], [("a", "z", 11)]);
        using var directory = new TemporaryDirectory();
        using var reader = OpenReader(directory.Path, "source.sst", source);
        var plan = FragmentPlan("a", "z", "m");

        var partitions = StreamingCompactionMerger.MergeAndPartition(
                [reader],
                plan,
                1024)
            .ToArray();

        Assert.Equal(2, partitions.Length);
        Assert.Empty(partitions.SelectMany(static partition => partition.Entries));
        Assert.Equal(
            [(Start: "a", End: "m", Sequence: 11UL)],
            partitions[0].RangeTombstones.Select(range => (
                Start: TestBytes.ToText(range.Start),
                End: TestBytes.ToText(range.End),
                range.Sequence)));
        Assert.Equal(
            [(Start: "m", End: "z", Sequence: 11UL)],
            partitions[1].RangeTombstones.Select(range => (
                Start: TestBytes.ToText(range.Start),
                End: TestBytes.ToText(range.End),
                range.Sequence)));
    }

    [Fact]
    public void ShouldAssignEntriesAtRangeBoundaryToTheRightFragment()
    {
        var source = Build(
            [("m", 4, "new"), ("m", 3, null)],
            [("a", "m", 1), ("m", "z", 2)]);
        using var directory = new TemporaryDirectory();
        using var reader = OpenReader(directory.Path, "source.sst", source);
        var plan = FragmentPlan("a", "z", "m");

        var partitions = StreamingCompactionMerger.MergeAndPartition(
                [reader],
                plan,
                1024)
            .ToArray();

        Assert.Equal(2, partitions.Length);
        Assert.Empty(partitions[0].Entries);
        Assert.Equal(
            [("m", 4UL, "new"), ("m", 3UL, (string?)null)],
            partitions[1].Entries.Select(entry => (
                TestBytes.ToText(entry.Key),
                entry.Sequence,
                entry.Value is null ? null : TestBytes.ToText(entry.Value))));
        Assert.Equal(("a", "m"), (
            TestBytes.ToText(Assert.Single(partitions[0].RangeTombstones).Start),
            TestBytes.ToText(partitions[0].RangeTombstones[0].End)));
        Assert.Equal(("m", "z"), (
            TestBytes.ToText(Assert.Single(partitions[1].RangeTombstones).Start),
            TestBytes.ToText(partitions[1].RangeTombstones[0].End)));
    }

    [Fact]
    public void ShouldKeepFinalKeyWithItsEndingRangeWithinOneFragmentOutput()
    {
        var source = Build(
            [("a", 1, "value"), ("m", 2, "value"), ("n", 3, "value"), ("z", 4, "value")],
            [("a", "z", 5)]);
        using var directory = new TemporaryDirectory();
        using var reader = OpenReader(directory.Path, "source.sst", source);
        var plan = FragmentPlan("a", "z", "m");

        var partitions = StreamingCompactionMerger.MergeAndPartition(
                [reader],
                plan,
                100)
            .ToArray();

        Assert.Equal(2, partitions.Length);
        Assert.Equal(
            ["m", "n", "z"],
            partitions[1].Entries.Select(static entry => TestBytes.ToText(entry.Key)));
        Assert.Equal(("m", "z"), (
            TestBytes.ToText(Assert.Single(partitions[1].RangeTombstones).Start),
            TestBytes.ToText(partitions[1].RangeTombstones[0].End)));
    }

    [Fact]
    public void ShouldDisposeFragmentStreamWhenSplitBoundaryExceedsMemoryBudget()
    {
        var stream = new TrackingEntryStream();
        var splitKey = new byte[2048];
        Array.Fill(splitKey, (byte)'b');
        var plan = FragmentPlan("a", "z", splitKey);
        var budget = new ResourceBudget(1024);

        var exception = Assert.Throws<PantsResourceLimitException>(() =>
            StreamingCompactionMerger.MergeAndPartition(
                    [stream],
                    [],
                    plan,
                    1024,
                    budget)
                .ToArray());

        Assert.Equal(PantsErrorCode.ResourceLimit, exception.Code);
        Assert.True(stream.IsDisposed);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public void ShouldChargeInputBlocksAndKeepOnlyOneOutputPartitionReservedAtATime()
    {
        var entries = Enumerable.Range(0, 100)
            .Select(index => ($"key-{index:D4}", (ulong)(index + 1), (string?)new string('v', 200)))
            .ToArray();
        var file = Build(entries, []);
        using var directory = new TemporaryDirectory();
        using var reader = OpenReader(directory.Path, "input.sst", file);
        var budget = new ResourceBudget(64 * 1024);
        var partitionCount = 0;
        long largestOutputBytes = 0;

        foreach (var partition in StreamingCompactionMerger.MergeAndPartition(
                     [reader],
                     Plan(),
                     2048,
                     budget))
        {
            partitionCount++;
            Assert.NotEmpty(partition.Entries);
            var outputBytes = partition.Entries.Sum(static entry =>
                (long)entry.Key.Length + (entry.Value?.Length ?? 0) + 32);
            largestOutputBytes = Math.Max(largestOutputBytes, outputBytes);
            Assert.InRange(budget.Current, outputBytes, budget.Limit);
        }

        Assert.True(partitionCount > 1);
        Assert.Equal(0, budget.Current);
        Assert.InRange(budget.Peak, largestOutputBytes + 1, budget.Limit);
    }

    /// <summary>
    ///     The output target only decides where partitions are cut. An entry larger than it, but
    ///     within the merge's memory, becomes its own indivisible partition for the caller's staging
    ///     window to admit or refuse, rather than failing as a memory limit it never reached.
    /// </summary>
    [Fact]
    public void ShouldEmitAnEntryLargerThanTheTargetAsItsOwnPartitionWhenMemoryAllows()
    {
        var file = Build(("a", 1, "small"), ("b", 2, new string('v', 4096)), ("c", 3, "small"));
        using var directory = new TemporaryDirectory();
        using var reader = OpenReader(directory.Path, "input.sst", file);
        var budget = new ResourceBudget(64 * 1024);

        var partitions = StreamingCompactionMerger.MergeAndPartition([reader], Plan(), 1024, budget)
            .Select(static partition => partition.Entries.Select(static entry => TestBytes.ToText(entry.Key)).ToArray())
            .ToArray();

        Assert.Equal([["a"], ["b"], ["c"]], partitions);
        Assert.Equal(0, budget.Current);
    }

    [Fact]
    public void ShouldDisposeEntryStreamsWhenRangeTombstoneAdmissionFails()
    {
        var stream = new TrackingEntryStream();
        var tombstones = Enumerable.Range(0, 128)
            .Select(index => new RangeTombstone(
                TestBytes.FromString($"a{index:D3}"),
                TestBytes.FromString($"b{index:D3}"),
                (ulong)(index + 1)))
            .ToArray();
        var budget = new ResourceBudget(1024);

        var exception = Assert.Throws<PantsResourceLimitException>(() =>
            StreamingCompactionMerger.MergeAndPartition(
                    [stream],
                    tombstones,
                    Plan(),
                    1024,
                    budget)
                .ToArray());

        Assert.Equal(PantsErrorCode.ResourceLimit, exception.Code);
        Assert.True(stream.IsDisposed);
        Assert.Equal(0, budget.Current);
    }

    static void AssertEquivalent(
        IReadOnlyList<SstContents> contents,
        CompactionPlan plan,
        long targetSizeBytes)
    {
        var expectedMerged = CompactionMerger.Merge(contents, plan);
        var expected = CompactionOutputPartitioner.Partition(expectedMerged, targetSizeBytes);

        using var directory = new TemporaryDirectory();
        var readers = contents
            .Select((content, index) => OpenReader(directory.Path, $"input-{index}.sst", content))
            .ToArray();
        try
        {
            var actual = StreamingCompactionMerger.MergeAndPartition(readers, plan, targetSizeBytes)
                .ToArray();

            Assert.Equal(expected.Count, actual.Length);
            for (var index = 0; index < expected.Count; index++)
            {
                AssertPartitionEqual(expected[index], actual[index]);
            }
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    static void AssertPartitionEqual(CompactionMergeResult expected, CompactionMergeResult actual)
    {
        Assert.Equal(
            expected.Entries.Select(e => (Key: TestBytes.ToText(e.Key), e.Sequence, e.IsDelete)),
            actual.Entries.Select(e => (Key: TestBytes.ToText(e.Key), e.Sequence, e.IsDelete)));
        Assert.Equal(
            expected.Entries.Select(e => e.Value is null ? null : TestBytes.ToText(e.Value)),
            actual.Entries.Select(e => e.Value is null ? null : TestBytes.ToText(e.Value)));
        Assert.Equal(
            expected.RangeTombstones.Select(t =>
                (Start: TestBytes.ToText(t.Start), End: TestBytes.ToText(t.End), t.Sequence)),
            actual.RangeTombstones.Select(t =>
                (Start: TestBytes.ToText(t.Start), End: TestBytes.ToText(t.End), t.Sequence)));
    }

    static SstReader OpenReader(string directory, string fileName, SstContents content)
    {
        var path = Path.Combine(directory, fileName);
        WriteSst(directory, fileName, content);
        return SstReader.Open(path);
    }

    static void WriteSst(string directory, string fileName, SstContents content)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(
            path,
            SstCodec.Encode(
                content.Entries.ToArray(),
                content.RangeTombstones.ToArray(),
                PantsPerformanceGoal.Latency));
    }

    static CompactionPlan Plan(
        long? snapshotHorizon = null,
        bool pointTombstoneGcEligible = true,
        bool rangeTombstoneGcEligible = true) => new(
        0,
        1,
        0,
        snapshotHorizon,
        pointTombstoneGcEligible,
        rangeTombstoneGcEligible,
        []);

    static CompactionPlan FragmentPlan(string smallest, string largest, string splitKey) =>
        FragmentPlan(smallest, largest, TestBytes.FromString(splitKey));

    static CompactionPlan FragmentPlan(string smallest, string largest, byte[] splitKey) => new(
        2,
        2,
        0,
        4,
        false,
        false,
        [new FileMeta
        {
            Name = "source.sst",
            Level = 2,
            ColumnFamilyId = 0,
            SmallestKey = TestBytes.FromString(smallest),
            LargestKey = TestBytes.FromString(largest),
            KeyBoundsComplete = true
        }],
        CompactionPurpose.Fragment,
        splitKey);

    static SstContents Build(params (string Key, ulong Sequence, string? Value)[] entries) =>
        Build(entries, []);

    static SstContents Build(
        (string Key, ulong Sequence, string? Value)[] entries,
        (string Start, string End, ulong Sequence)[] tombstones)
    {
        var sstEntries = entries
            .Select(e => new SstEntry(
                TestBytes.FromString(e.Key),
                e.Value is null ? null : TestBytes.FromString(e.Value),
                e.Sequence,
                null,
                e.Value is null))
            .ToArray();
        var rangeTombstones = tombstones
            .Select(t => new RangeTombstone(
                TestBytes.FromString(t.Start),
                TestBytes.FromString(t.End),
                t.Sequence))
            .ToArray();
        return new SstContents(sstEntries, rangeTombstones, 1);
    }

    sealed class TrackingEntryStream : ICompactionEntryStream
    {
        readonly SstEntry[] _entries;
        int _index = -1;

        public TrackingEntryStream(params SstEntry[] entries) => _entries = entries;

        public SstEntry Current => _index >= 0 && _index < _entries.Length
            ? _entries[_index]
            : throw new InvalidOperationException("The stream has no current entry.");

        public bool IsDisposed { get; private set; }

        public bool MoveNext() => ++_index < _entries.Length;

        public void Dispose() => IsDisposed = true;
    }
}
