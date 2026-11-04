namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

static class CompactionMoves
{
    // The pinned public Midge compactor's whole-file grandparent overlap allowance.
    const ulong MaximumGrandparentOverlapBytes = 320UL * 1024 * 1024;

    public static bool IsEligible(CompactionPlan plan, ManifestReadSnapshot manifest, long targetSstSizeBytes)
    {
        if (plan.Purpose != CompactionPurpose.Merge || plan.Inputs.Count == 0 ||
            plan.Inputs.Any(file => file.Level != plan.SourceLevel || file.ColumnFamilyId != plan.ColumnFamilyId ||
                                   file.SizeBytes > (ulong)targetSstSizeBytes))
        {
            return false;
        }
        foreach (var file in plan.Inputs)
        {
            if (!file.HasTrustedKeyBounds())
            {
                return false;
            }
            ulong bytes = 0;
            foreach (var grandparent in manifest.Files.Where(other =>
                         other.ColumnFamilyId == plan.ColumnFamilyId && other.Level == plan.TargetLevel + 1 &&
                         (!other.HasTrustedKeyBounds() ||
                          other.SmallestKey!.AsSpan().SequenceCompareTo(file.LargestKey) <= 0 &&
                          file.SmallestKey!.AsSpan().SequenceCompareTo(other.LargestKey) <= 0)))
            {
                if (grandparent.SizeBytes > MaximumGrandparentOverlapBytes - bytes)
                {
                    return false;
                }
                bytes += grandparent.SizeBytes;
            }
        }
        try
        {
            _ = ManifestTransitions.ValidateMove(
                manifest.Files, manifest.ColumnFamilies, plan.Inputs, plan.TargetLevel, out var alreadyMoved);
            return !alreadyMoved;
        }
        catch (PantsCorruptionException)
        {
            return false;
        }
    }
}
