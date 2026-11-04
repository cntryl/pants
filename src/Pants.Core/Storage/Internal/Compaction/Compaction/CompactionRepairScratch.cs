namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

/// <summary>
///     Reduces a large repair component through bounded raw merge passes. Scratch partitions are
///     never manifest-authoritative and use the existing startup-cleaned staging directory.
/// </summary>
sealed class CompactionRepairScratch : IDisposable
{
    readonly string _directory;
    readonly long _capacityBytes;
    readonly PantsPerformanceGoal _performanceGoal;
    readonly Func<string, SstReader> _openReader;
    readonly BackgroundWriteLimiter? _writeLimiter;
    readonly Dictionary<string, long> _paths = new(StringComparer.Ordinal);
    long _usedBytes;

    public CompactionRepairScratch(
        string sstDirectory,
        long? stagingWindowBytes,
        PantsPerformanceGoal performanceGoal,
        Func<string, SstReader>? openReader = null,
        BackgroundWriteLimiter? writeLimiter = null)
    {
        _directory = Path.Combine(sstDirectory, ".flush-staging");
        StoragePathGuard.EnsureNotLink(_directory);
        if (!IsCleanupVerified(sstDirectory))
        {
            throw PantsException.ResourceLimit("Retained overlap-repair scratch requires cleanup before another repair.");
        }

        _capacityBytes = stagingWindowBytes is { } window
            ? window / 2
            : LocalCapacity(sstDirectory);
        _performanceGoal = performanceGoal;
        _openReader = openReader ?? SstReader.Open;
        _writeLimiter = writeLimiter;
        if (_capacityBytes <= 0)
        {
            throw PantsException.ResourceLimit("Overlap repair has no admitted local scratch capacity.");
        }
    }

    public long UsedBytes => _usedBytes;

    public static bool IsCleanupVerified(string sstDirectory)
    {
        var directory = Path.Combine(sstDirectory, ".flush-staging");
        try
        {
            StoragePathGuard.EnsureNotLink(directory);
            return !Directory.EnumerateFileSystemEntries(directory, "repair.*.tmp").Any();
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PantsInvalidPathException)
        {
            return false;
        }
    }

    public CompactionStreams Reduce(
        CompactionPlan plan,
        Func<FileMeta, ICompactionFileCursor> openInput,
        int fanIn,
        long partitionTargetBytes,
        ResourceBudget? budget,
        CancellationToken cancellationToken)
    {
        if (fanIn < 2)
        {
            throw PantsException.ResourceLimit("Overlap repair requires at least two admitted merge streams.");
        }

        IReadOnlyList<IReadOnlyList<FileMeta>> runs = plan.Inputs
            .Select(static file => (IReadOnlyList<FileMeta>)new[] { file }).ToArray();
        while (runs.Count > fanIn)
        {
            var next = new List<IReadOnlyList<FileMeta>>();
            foreach (var batch in runs.Chunk(fanIn))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (batch.Length == 1)
                {
                    next.Add(batch[0]);
                    continue;
                }

                var outputs = new List<FileMeta>();
                using (var streams = CompactionStreams.CreateFromRuns(batch, OpenFile, budget))
                {
                    // Zero is below every committed sequence. Retain the entire raw history in
                    // intermediate runs; only the final repair may apply ordinary MVCC retention.
                    var rawPlan = plan with { SnapshotHorizon = 0 };
                    foreach (var partition in StreamingCompactionMerger.MergeAndPartition(
                                 streams.Streams,
                                 streams.RangeTombstones,
                                 rawPlan,
                                 partitionTargetBytes,
                                 budget,
                                 cancellationToken))
                    {
                        outputs.Add(WritePartition(partition, plan));
                    }
                }

                foreach (var input in batch.SelectMany(static run => run))
                {
                    DeleteOwned(input.Name);
                }

                next.Add(outputs);
            }

            runs = next;
        }

        return CompactionStreams.CreateFromRuns(runs, OpenFile, budget);

        ICompactionFileCursor OpenFile(FileMeta input) => _paths.ContainsKey(input.Name)
            ? new LocalFileCursor(_openReader(input.Name), budget)
            : openInput(input);
    }

    public void Dispose()
    {
        Exception? failure = null;
        foreach (var path in _paths.Keys.ToArray())
        {
            try
            {
                DeleteOwned(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failure ??= exception;
            }
        }

        if (failure is not null)
        {
            throw new PantsIOException("Overlap repair could not remove its non-authoritative scratch files.", failure);
        }
    }

    FileMeta WritePartition(CompactionMergeResult partition, CompactionPlan plan)
    {
        var path = Path.Combine(_directory, $"repair.{Guid.NewGuid():N}.tmp");
        _paths.Add(path, 0);
        long size;
        using (var output = new CompactionScratchWriteStream(path, _capacityBytes - _usedBytes))
        {
            SstCodec.EncodeTo(_writeLimiter is { } limiter ? new BackgroundRateWriteStream(output, limiter) : output,
                partition.Entries, partition.RangeTombstones, _performanceGoal);
            output.Flush(true);
            size = output.Length;
        }

        _paths[path] = size;
        _usedBytes = checked(_usedBytes + size);
        var (smallest, largest) = SstCodec.GetKeyBounds(partition.Entries, partition.RangeTombstones);
        return new FileMeta
        {
            Name = path,
            ColumnFamilyId = plan.ColumnFamilyId,
            Level = plan.SourceLevel,
            SizeBytes = checked((ulong)size),
            SmallestKey = smallest,
            LargestKey = largest,
            KeyBoundsComplete = true
        };
    }

    void DeleteOwned(string path)
    {
        if (!_paths.TryGetValue(path, out var size))
        {
            return;
        }

        TransientSharingRetry.Delete(path);
        _paths.Remove(path);
        _usedBytes -= size;
    }

    static long LocalCapacity(string sstDirectory)
    {
        var drivePath = OperatingSystem.IsWindows()
            ? Path.GetPathRoot(Path.GetFullPath(sstDirectory))!
            : sstDirectory;
        return new DriveInfo(drivePath).AvailableFreeSpace / 8;
    }
}
