namespace Cntryl.Pants.Storage.Internal.Wal;

/// <summary>
///     Decides whether every mutation in a sealed WAL segment is already durable in a manifest SST,
///     using the same rule recovery uses to skip replayed mutations.
/// </summary>
static class WalSegmentCoverage
{
    /// <param name="persistedFamilySequences">Largest SST-covered sequence per column family.</param>
    /// <param name="activeFamilyIds">Families that still exist; mutations of others are covered.</param>
    /// <param name="scratchDirectory">Where large open transactions spool while being evaluated.</param>
    public static WalSegmentCoverageResult Evaluate(
        ReadOnlySpan<byte> segmentBytes,
        IReadOnlyDictionary<uint, ulong> persistedFamilySequences,
        IReadOnlySet<uint> activeFamilyIds,
        string scratchDirectory)
    {
        ArgumentNullException.ThrowIfNull(persistedFamilySequences);
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
                    (mutation, _) =>
                    {
                        if (activeFamilyIds.Contains(mutation.ColumnFamilyId) &&
                            mutation.Sequence > persistedFamilySequences.GetValueOrDefault(
                                mutation.ColumnFamilyId))
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
    ///     Picks the covered segments that are safe to delete. A retained segment holding a lower
    ///     writer epoch than a deleted earlier one could be a stale writer's record that only the
    ///     deleted segment's higher epoch kept ignored, so that earlier segment is retained too.
    /// </summary>
    /// <param name="orderedSegments">Segment results in ascending segment order.</param>
    public static bool[] SelectPrunable(IReadOnlyList<WalSegmentCoverageResult> orderedSegments)
    {
        ArgumentNullException.ThrowIfNull(orderedSegments);
        var prunable = orderedSegments.Select(static segment => segment.Covered).ToArray();
        bool changed;
        do
        {
            changed = false;
            for (var retained = 0; retained < prunable.Length; retained++)
            {
                if (prunable[retained])
                {
                    continue;
                }

                for (var earlier = 0; earlier < retained; earlier++)
                {
                    if (prunable[earlier] &&
                        orderedSegments[earlier].MaximumWriterEpoch >
                        orderedSegments[retained].MinimumWriterEpoch)
                    {
                        prunable[earlier] = false;
                        changed = true;
                    }
                }
            }
        }
        while (changed);

        return prunable;
    }
}

readonly record struct WalSegmentCoverageResult(
    bool Covered,
    ulong MinimumWriterEpoch,
    ulong MaximumWriterEpoch);
