namespace Cntryl.Pants.Storage.Internal.Wal;

/// <summary>
///     Decides whether every committed transaction in a sealed WAL segment is below the durable
///     per-family flush frontier, using the same boundary recovery uses to skip replay.
/// </summary>
static class WalSegmentCoverage
{
    /// <param name="flushedThrough">Durable commit-sequence frontier per column family.</param>
    /// <param name="activeFamilyIds">Families that still exist; mutations of others are covered.</param>
    /// <param name="scratchDirectory">Where large open transactions spool while being evaluated.</param>
    public static WalSegmentCoverageResult Evaluate(
        ReadOnlySpan<byte> segmentBytes,
        IReadOnlyDictionary<uint, ulong> flushedThrough,
        IReadOnlySet<uint> activeFamilyIds,
        string scratchDirectory)
    {
        ArgumentNullException.ThrowIfNull(flushedThrough);
        ArgumentNullException.ThrowIfNull(activeFamilyIds);
        ulong minimumEpoch = ulong.MaxValue;
        ulong maximumEpoch = 0;
        var covered = true;
        using var recovery = new WalRecoveryStateMachine(scratchDirectory);
        WalFrameReader.Visit(
            segmentBytes,
            (record, _) =>
            {
                minimumEpoch = Math.Min(minimumEpoch, record.WriterEpoch);
                maximumEpoch = Math.Max(maximumEpoch, record.WriterEpoch);
                recovery.Accept(
                    record,
                    (mutation, commitSequence) =>
                    {
                        if (activeFamilyIds.Contains(mutation.ColumnFamilyId) &&
                            commitSequence > flushedThrough.GetValueOrDefault(mutation.ColumnFamilyId))
                        {
                            covered = false;
                        }
                    });
            });
        return new WalSegmentCoverageResult(
            covered && !recovery.HasOpenTransactions,
            minimumEpoch,
            maximumEpoch);
    }

    /// <summary>
    ///     Picks only the covered oldest prefix. A retained segment holding a lower writer epoch
    ///     than a deleted earlier one could be a stale writer's record that only the deleted
    ///     segment's higher epoch kept ignored, so that earlier segment is retained too.
    /// </summary>
    /// <param name="orderedSegments">Segment results in ascending segment order.</param>
    public static bool[] SelectPrunable(IReadOnlyList<WalSegmentCoverageResult> orderedSegments)
    {
        ArgumentNullException.ThrowIfNull(orderedSegments);
        var prefixLength = 0;
        while (prefixLength < orderedSegments.Count && orderedSegments[prefixLength].Covered)
        {
            prefixLength++;
        }

        for (var candidate = 0; candidate < prefixLength; candidate++)
        {
            for (var retained = prefixLength; retained < orderedSegments.Count; retained++)
            {
                if (orderedSegments[candidate].MaximumWriterEpoch >
                    orderedSegments[retained].MinimumWriterEpoch)
                {
                    prefixLength = candidate;
                    break;
                }
            }

            if (candidate >= prefixLength)
            {
                break;
            }
        }

        return Enumerable.Range(0, orderedSegments.Count)
            .Select(index => index < prefixLength)
            .ToArray();
    }
}

readonly record struct WalSegmentCoverageResult(
    bool Covered,
    ulong MinimumWriterEpoch,
    ulong MaximumWriterEpoch);
