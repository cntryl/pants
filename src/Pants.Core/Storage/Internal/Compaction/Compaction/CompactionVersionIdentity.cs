namespace Cntryl.Pants.Storage.Internal.Compaction.Compaction;

static class CompactionVersionIdentity
{
    public static void RequireMatchingContent(SstEntry first, SstEntry duplicate)
    {
        if (first.IsDelete != duplicate.IsDelete ||
            first.Expiration != duplicate.Expiration ||
            (first.Value is null) != (duplicate.Value is null) ||
            !first.Value.AsSpan().SequenceEqual(duplicate.Value))
        {
            throw new PantsCorruptionException(
                $"Compaction inputs contain conflicting content for the same key at sequence {first.Sequence}.");
        }
    }
}
