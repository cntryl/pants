namespace Cntryl.Pants.Storage;

public sealed class SstReadViewTests
{
    /// <summary>
    ///     Levels below zero are non-overlapping by construction, so a point read should reach the
    ///     one file that can hold the key without inspecting the rest of the level.
    /// </summary>
    [Fact]
    public void ShouldBoundPointCandidateWorkAsLevelCardinalityGrows()
    {
        var view = SstReadView.Create(BuildLevel(0, 4096));

        var candidates = view.SelectPointCandidates(0, Key(2048), out var filesExamined);

        Assert.Equal("l1-2048.sst", Assert.Single(candidates).Name);
        Assert.True(
            filesExamined < 64,
            $"A point read inspected {filesExamined} of 4096 files; the level is sorted and " +
            "non-overlapping, so the search should be logarithmic.");
    }

    [Fact]
    public void ShouldReturnEveryLevelZeroCandidateNewestFirst()
    {
        FileMeta[] files =
        [
            File("l0-a.sst", 0, 1, "a", "z"),
            File("l0-b.sst", 0, 2, "a", "z"),
            File("l0-c.sst", 0, 3, "m", "z")
        ];
        var view = SstReadView.Create(files);

        var candidates = view.SelectPointCandidates(0, Key("b"), out _);

        Assert.Equal(["l0-b.sst", "l0-a.sst"], candidates.Select(static file => file.Name));
    }

    /// <summary>
    ///     A manifest that claims a non-overlapping level but does not deliver one must not silently
    ///     lose a candidate; the level falls back to a full scan instead.
    /// </summary>
    [Fact]
    public void ShouldScanALevelWhoseFilesOverlap()
    {
        FileMeta[] files =
        [
            File("l1-a.sst", 1, 1, "a", "m"),
            File("l1-b.sst", 1, 2, "f", "z")
        ];
        var view = SstReadView.Create(files);

        var candidates = view.SelectPointCandidates(0, Key("h"), out _);

        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void ShouldIsolateColumnFamilies()
    {
        FileMeta[] files =
        [
            File("a.sst", 1, 1, "a", "z"),
            File("b.sst", 1, 2, "a", "z", columnFamilyId: 7)
        ];
        var view = SstReadView.Create(files);

        Assert.Equal("a.sst", Assert.Single(view.SelectPointCandidates(0, Key("m"), out _)).Name);
        Assert.Equal("b.sst", Assert.Single(view.SelectPointCandidates(7, Key("m"), out _)).Name);
        Assert.Empty(view.SelectPointCandidates(9, Key("m"), out _));
    }

    /// <summary>
    ///     The index must agree with the exhaustive scan it replaces, for every key, including keys
    ///     that fall in the gaps between files.
    /// </summary>
    [Fact]
    public void ShouldAgreeWithAnExhaustiveScanAcrossEveryKey()
    {
        var files = BuildLevel(0, 64).Concat(
        [
            File("l0-1.sst", 0, 9001, "", "0"),
            File("l0-2.sst", 0, 9002, "", "@")
        ]).ToArray();
        var view = SstReadView.Create(files);

        for (var value = 0; value < 80; value++)
        {
            var key = Key(value);
            var expected = files
                .Where(file => LocalDiskStore.IsWithinFileRange(file, key))
                .OrderByDescending(static file => file.SstSequence)
                .Select(static file => file.Name)
                .ToHashSet(StringComparer.Ordinal);

            var actual = view.SelectPointCandidates(0, key, out _)
                .Select(static file => file.Name)
                .ToHashSet(StringComparer.Ordinal);

            Assert.True(
                expected.SetEquals(actual),
                $"Key {value}: expected [{string.Join(", ", expected)}] but got " +
                $"[{string.Join(", ", actual)}].");
        }
    }

    static FileMeta[] BuildLevel(uint columnFamilyId, int count) =>
        Enumerable.Range(0, count)
            .Select(index => new FileMeta
            {
                Name = $"l1-{index}.sst",
                Level = 1,
                ColumnFamilyId = columnFamilyId,
                SstSequence = checked((ulong)index + 1),
                SizeBytes = 1024,
                SmallestKey = KeyValues(index),
                LargestKey = KeyValues(index)
            })
            .ToArray();

    static int[] KeyValues(int value) =>
    [
        (value >> 16) & 0xFF,
        (value >> 8) & 0xFF,
        value & 0xFF
    ];

    static byte[] Key(int value) => KeyValues(value).Select(static v => (byte)v).ToArray();

    static byte[] Key(string value) => System.Text.Encoding.UTF8.GetBytes(value);

    static FileMeta File(
        string name,
        uint level,
        ulong sequence,
        string smallest,
        string largest,
        uint columnFamilyId = 0) => new()
        {
            Name = name,
            Level = level,
            ColumnFamilyId = columnFamilyId,
            SstSequence = sequence,
            SizeBytes = 1024,
            SmallestKey = System.Text.Encoding.UTF8.GetBytes(smallest)
            .Select(static value => (int)value).ToArray(),
            LargestKey = System.Text.Encoding.UTF8.GetBytes(largest)
            .Select(static value => (int)value).ToArray()
        };
}
