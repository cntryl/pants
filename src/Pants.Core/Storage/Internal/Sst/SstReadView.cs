namespace Cntryl.Pants.Storage.Internal.Sst;

/// <summary>
///     A manifest's published SSTs, indexed for candidate selection.
/// </summary>
/// <remarks>
///     Selecting candidates by filtering every visible file makes read work grow with the size of
///     the family rather than with the levels a key can actually be in. Levels below zero are
///     non-overlapping by construction, so once their files are sorted a key can only fall in one of
///     them and a binary search finds it. Level zero genuinely overlaps and is still scanned, but it
///     is bounded by the admission ceiling.
///     Built once per manifest change and shared by every snapshot taken against it, so the sort
///     cost is paid when files are published rather than on each query. Key bounds are decoded here
///     for the same reason.
/// </remarks>
sealed class SstReadView
{
    static readonly SstReadView EmptyView = new(new Dictionary<uint, FamilyView>());

    readonly IReadOnlyDictionary<uint, FamilyView> _families;

    SstReadView(IReadOnlyDictionary<uint, FamilyView> families)
    {
        _families = families;
    }

    public static SstReadView Empty => EmptyView;

    public static SstReadView Create(IReadOnlyList<FileMeta> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            return EmptyView;
        }

        var families = new Dictionary<uint, FamilyView>();
        foreach (var group in files.GroupBy(static file => file.ColumnFamilyId))
        {
            families[group.Key] = FamilyView.Create(group);
        }

        return new SstReadView(families);
    }

    /// <summary>
    ///     Files that may hold <paramref name="key" />, newest first.
    /// </summary>
    /// <param name="filesExamined">
    ///     How many files the search inspected. Exposed so the work bound can be asserted rather
    ///     than assumed.
    /// </param>
    public IReadOnlyList<FileMeta> SelectPointCandidates(
        uint columnFamilyId,
        ReadOnlySpan<byte> key,
        out int filesExamined)
    {
        filesExamined = 0;
        if (!_families.TryGetValue(columnFamilyId, out var family))
        {
            return [];
        }

        return family.SelectPointCandidates(key, ref filesExamined);
    }

    /// <summary>One level's files for one column family.</summary>
    sealed class LevelView
    {
        readonly IndexedFile[] _files;

        LevelView(IndexedFile[] files, bool searchable)
        {
            _files = files;
            Searchable = searchable;
        }

        /// <summary>
        ///     Whether the level's files are sorted and non-overlapping, so a binary search is sound.
        ///     A level that fails this is scanned instead: trusting a manifest that claims a layout
        ///     it does not have would silently drop candidates and lose data on read.
        /// </summary>
        bool Searchable { get; }

        public static LevelView Create(IEnumerable<FileMeta> files, bool allowSearch)
        {
            var indexed = files
                .Select(IndexedFile.Create)
                .OrderBy(static file => file.SmallestKey, ByteArrayComparer.Instance)
                .ThenBy(static file => file.File.Name, StringComparer.Ordinal)
                .ToArray();
            return new LevelView(indexed, allowSearch && IsNonOverlapping(indexed));
        }

        public void AddPointCandidates(
            ReadOnlySpan<byte> key,
            List<FileMeta> candidates,
            ref int filesExamined)
        {
            if (!Searchable)
            {
                foreach (var indexed in _files)
                {
                    filesExamined++;
                    if (indexed.Contains(key))
                    {
                        candidates.Add(indexed.File);
                    }
                }

                return;
            }

            var low = 0;
            var high = _files.Length - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                filesExamined++;
                var indexed = _files[middle];
                if (key.SequenceCompareTo(indexed.SmallestKey) < 0)
                {
                    high = middle - 1;
                }
                else if (key.SequenceCompareTo(indexed.LargestKey) > 0)
                {
                    low = middle + 1;
                }
                else
                {
                    candidates.Add(indexed.File);
                    return;
                }
            }
        }

        static bool IsNonOverlapping(IndexedFile[] files)
        {
            for (var index = 1; index < files.Length; index++)
            {
                if (ByteArrayComparer.Instance.Compare(
                        files[index].SmallestKey,
                        files[index - 1].LargestKey) <= 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    sealed class FamilyView
    {
        readonly LevelView[] _levels;

        FamilyView(LevelView[] levels)
        {
            _levels = levels;
        }

        public static FamilyView Create(IEnumerable<FileMeta> files)
        {
            var byLevel = files.GroupBy(static file => file.Level).ToArray();
            var maximumLevel = byLevel.Max(static group => group.Key);
            var levels = new LevelView[maximumLevel + 1];
            for (var level = 0U; level <= maximumLevel; level++)
            {
                var group = byLevel.FirstOrDefault(candidate => candidate.Key == level);

                // Level zero overlaps by construction; only deeper levels can be searched.
                levels[level] = LevelView.Create(
                    group ?? Enumerable.Empty<FileMeta>(),
                    level != 0);
            }

            return new FamilyView(levels);
        }

        public List<FileMeta> SelectPointCandidates(
            ReadOnlySpan<byte> key,
            ref int filesExamined)
        {
            var candidates = new List<FileMeta>();
            foreach (var level in _levels)
            {
                level.AddPointCandidates(key, candidates, ref filesExamined);
            }

            candidates.Sort(static (left, right) => right.SstSequence.CompareTo(left.SstSequence));
            return candidates;
        }
    }

    /// <summary>A file with its key bounds decoded once.</summary>
    readonly struct IndexedFile
    {
        IndexedFile(FileMeta file, byte[] smallestKey, byte[] largestKey)
        {
            File = file;
            SmallestKey = smallestKey;
            LargestKey = largestKey;
        }

        public FileMeta File { get; }

        public byte[] SmallestKey { get; }

        public byte[] LargestKey { get; }

        public static IndexedFile Create(FileMeta file) => new(
            file,
            LocalDiskStore.GetMetadataKey(file.SmallestKey ?? []),
            LocalDiskStore.GetMetadataKey(file.LargestKey ?? []));

        public bool Contains(ReadOnlySpan<byte> key) =>
            key.SequenceCompareTo(SmallestKey) >= 0 &&
            key.SequenceCompareTo(LargestKey) <= 0;
    }
}
