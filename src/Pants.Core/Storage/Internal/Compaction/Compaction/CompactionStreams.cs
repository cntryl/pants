namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

/// <summary>The merge streams and range tombstones for one compaction plan.</summary>
sealed class CompactionStreams : IDisposable
{
    const int RangeTombstoneOverheadBytes = 24;

    readonly List<IDisposable> _reservations = [];

    CompactionStreams(
        IReadOnlyList<ICompactionEntryStream> streams,
        IReadOnlyList<RangeTombstone> rangeTombstones)
    {
        Streams = streams;
        RangeTombstones = rangeTombstones;
    }

    public IReadOnlyList<ICompactionEntryStream> Streams { get; }

    public IReadOnlyList<RangeTombstone> RangeTombstones { get; }

    public void Dispose()
    {
        foreach (var stream in Streams)
        {
            stream.Dispose();
        }

        foreach (var reservation in _reservations)
        {
            reservation.Dispose();
        }
    }

    /// <summary>
    ///     Builds one stream per level-0 source file, one chained stream for a sorted inner-level
    ///     source set, and one chained stream for the complete target span, so simultaneously open
    ///     readers never exceed the source stream count plus one.
    /// </summary>
    /// <param name="maximumSourceStreams">
    ///     Cap on simultaneous source streams. A plan above it fails with a resource-limit error
    ///     rather than opening that many readers; the planner already shrinks sources to fit.
    /// </param>
    /// <remarks>
    ///     Range tombstones must be known before merging, so each input's metadata is read once and
    ///     released; the tombstones kept are charged to <paramref name="resourceBudget" />.
    /// </remarks>
    public static CompactionStreams Create(
        CompactionPlan plan,
        Func<FileMeta, string> pathOf,
        Func<string, SstReader> openReader,
        int maximumSourceStreams,
        ResourceBudget? resourceBudget) =>
        Create(
            plan,
            file => new LocalFileCursor(openReader(pathOf(file)), resourceBudget),
            maximumSourceStreams,
            resourceBudget);

    /// <param name="openFile">Opens one input, locally or through remote ranges.</param>
    public static CompactionStreams Create(
        CompactionPlan plan,
        Func<FileMeta, ICompactionFileCursor> openFile,
        int maximumSourceStreams,
        ResourceBudget? resourceBudget)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var sources = plan.Inputs.Where(file => file.Level == plan.SourceLevel).ToArray();
        var targets = plan.Inputs.Where(file => file.Level != plan.SourceLevel).ToArray();
        var sourceGroups = plan.SourceLevel != 0 && TryOrderDisjoint(sources, out var orderedSources)
            ? [orderedSources]
            : sources.Select(static file => new[] { file }).ToArray();
        if (sourceGroups.Length > maximumSourceStreams)
        {
            throw PantsException.ResourceLimit(
                $"A compaction needs {sourceGroups.Length} simultaneous source streams, above the " +
                $"limit of {maximumSourceStreams}.");
        }

        var targetGroups = targets.Length == 0
            ? []
            : TryOrderDisjoint(targets, out var orderedTargets)
                ? [orderedTargets]
                : targets.Select(static file => new[] { file }).ToArray();
        var streams = new List<ICompactionEntryStream>();
        var tombstones = new List<RangeTombstone>();
        var result = new CompactionStreams(streams, tombstones);
        try
        {
            foreach (var group in sourceGroups.Concat(targetGroups))
            {
                streams.Add(new ChainedFileEntryStream(group, openFile));
            }

            foreach (var file in plan.Inputs)
            {
                using var cursor = openFile(file);
                foreach (var tombstone in cursor.RangeTombstones)
                {
                    if (resourceBudget is not null)
                    {
                        result._reservations.Add(resourceBudget.Reserve(checked(
                            (long)tombstone.Start.Length + tombstone.End.Length +
                            RangeTombstoneOverheadBytes)));
                    }

                    tombstones.Add(tombstone);
                }
            }

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Orders files by key range and reports whether they are pairwise disjoint, which is what
    ///     makes walking them back to back equivalent to merging them.
    /// </summary>
    static bool TryOrderDisjoint(FileMeta[] files, out FileMeta[] ordered)
    {
        ordered = [.. files];
        if (files.Any(static file => !file.HasTrustedKeyBounds()))
        {
            return false;
        }

        Array.Sort(
            ordered,
            static (left, right) => left.SmallestKey!.AsSpan().SequenceCompareTo(right.SmallestKey));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index].SmallestKey!.AsSpan().SequenceCompareTo(ordered[index - 1].LargestKey) <= 0)
            {
                return false;
            }
        }

        return true;
    }
}
