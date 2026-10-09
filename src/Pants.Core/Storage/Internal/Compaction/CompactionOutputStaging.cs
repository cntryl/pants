namespace Cntryl.Pants.Storage.Internal.Compaction;

/// <summary>
///     The fixed local window a hybrid compaction stages its outputs through. Each output partition
///     is written locally, published to cloud storage by <see cref="DrainAsync" />, and released
///     before the next is staged, so a compaction's local footprint is this window however large its
///     inputs and however many outputs it writes (mirrors Midge's
///     <c>reserve_compaction_staging_with_token</c>).
/// </summary>
/// <remarks>
///     The caller holds a local-disk reservation for <see cref="WindowBytes" /> for as long as the
///     compaction runs. Partitions are cut at half the window so the encoded file, which carries
///     index, filter and footer overhead beyond the merge's entry estimate, still fits; an
///     indivisible partition (one key's retained versions) that cannot fit the window is refused
///     with <see cref="PantsNoSpaceException" /> rather than overrunning the budget.
/// </remarks>
sealed class CompactionOutputStaging
{
    readonly CloudCompactionOutputPublisher _publisher;

    public CompactionOutputStaging(long windowBytes, CloudCompactionOutputPublisher publisher)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowBytes);
        WindowBytes = windowBytes;
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    public long WindowBytes { get; }

    public long PartitionTargetBytes => Math.Max(1, WindowBytes / 2);

    /// <summary>Refuses a partition whose estimated size alone cannot fit the window.</summary>
    public void AdmitPartition(long estimatedBytes)
    {
        if (estimatedBytes > WindowBytes)
        {
            throw NoSpace(estimatedBytes);
        }
    }

    /// <summary>
    ///     Publishes one staged output to cloud storage, after the compaction intent that names it,
    ///     so a failure at any point leaves an object recovery can attribute and roll back. The
    ///     caller records the intent locally first and releases the local copy once this returns,
    ///     before staging the next partition.
    /// </summary>
    public ValueTask DrainAsync(string outputName, long sizeBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputName);
        return sizeBytes > WindowBytes
            ? ValueTask.FromException(NoSpace(sizeBytes))
            : _publisher([outputName], cancellationToken);
    }

    PantsNoSpaceException NoSpace(long bytes) => new(
        $"A {bytes}-byte compaction output cannot fit the {WindowBytes}-byte local staging window.");
}
