namespace Cntryl.Pants.Storage.Internal.Wal;

sealed class MidgeWalRecoveredVersionTracker
{
    readonly Dictionary<(uint ColumnFamilyId, ulong Sequence), HashSet<byte[]>> _versions = [];

    public void ValidateAndRecord(IReadOnlyList<MidgeWalMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        var pending = new Dictionary<(uint ColumnFamilyId, ulong Sequence), HashSet<byte[]>>();
        foreach (var mutation in mutations)
        {
            if (!IsPointMutation(mutation.Operation))
            {
                continue;
            }

            var identity = (mutation.ColumnFamilyId, mutation.Sequence);
            if ((_versions.TryGetValue(identity, out var recoveredKeys) &&
                 recoveredKeys.Contains(mutation.Key)) ||
                !GetOrCreate(pending, identity).Add(mutation.Key))
            {
                throw new PantsStorageException(
                    $"WAL contains a duplicate logical key version for column family " +
                    $"{mutation.ColumnFamilyId} at sequence {mutation.Sequence}.");
            }
        }

        foreach (var (identity, keys) in pending)
        {
            var recoveredKeys = GetOrCreate(_versions, identity);
            foreach (var key in keys)
            {
                recoveredKeys.Add(key.ToArray());
            }
        }
    }

    static HashSet<byte[]> GetOrCreate(
        Dictionary<(uint ColumnFamilyId, ulong Sequence), HashSet<byte[]>> versions,
        (uint ColumnFamilyId, ulong Sequence) identity)
    {
        if (!versions.TryGetValue(identity, out var keys))
        {
            keys = new HashSet<byte[]>(ByteArrayComparer.Instance);
            versions.Add(identity, keys);
        }

        return keys;
    }

    static bool IsPointMutation(MidgeWalOperation operation) =>
        operation is MidgeWalOperation.Put or
            MidgeWalOperation.Insert or
            MidgeWalOperation.Delete;
}
