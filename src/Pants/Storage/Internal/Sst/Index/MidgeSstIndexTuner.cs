namespace Cntryl.Pants;

internal static class MidgeSstIndexTuner
{
    public static MidgeSstIndexKind Decide(KeyStructureProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.KeyCount < 128 ||
            profile.Entropy > 4.0F ||
            profile.PrefixDivergence >= 1024)
        {
            return MidgeSstIndexKind.Sparse;
        }

        if ((profile.AverageSharedPrefix >= 6.0F && profile.Entropy < 3.5F) ||
            profile.CommonPrefixLength >= 2 ||
            profile.MaximumSharedPrefix >= 8 ||
            profile.PrefixDivergence < 256 ||
            (profile.KeyLengthStandardDeviation > 5.0F && profile.AverageSharedPrefix >= 4.0F))
        {
            return MidgeSstIndexKind.Trie;
        }

        return MidgeSstIndexKind.Sparse;
    }
}
