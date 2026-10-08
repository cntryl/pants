namespace Cntryl.Pants.Storage.Internal.Wal;

/// <summary>
///     Detects a logical key version that appears twice during replay. A byte-identical copy in a
///     different WAL file is skipped (the reference engine writes such duplicates when a segment is
///     republished); different content for the same version, or any repeat inside one file, is
///     corruption.
/// </summary>
sealed class WalRecoveredVersionTracker
{
    readonly Dictionary<(uint ColumnFamilyId, ulong Sequence), Dictionary<byte[], Recovered>> _versions = [];

    /// <summary>
    ///     Forgets recorded versions once a recovery checkpoint has made them durable, which bounds
    ///     this tracker's memory by the checkpoint interval instead of the whole WAL backlog.
    /// </summary>
    public void Clear() => _versions.Clear();

    /// <summary>
    ///     Validates <paramref name="mutations" /> from <paramref name="source" /> and returns the
    ///     ones to apply, which excludes identical duplicates first recovered from another file.
    /// </summary>
    public IReadOnlyList<WalMutation> ValidateAndRecord(
        IReadOnlyList<WalMutation> mutations,
        string source)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(source);
        var pending = new Dictionary<(uint ColumnFamilyId, ulong Sequence), Dictionary<byte[], Recovered>>();
        var skipped = new HashSet<WalMutation>(ReferenceEqualityComparer.Instance);
        foreach (var mutation in mutations)
        {
            if (!IsPointMutation(mutation.Operation))
            {
                continue;
            }

            var identity = (mutation.ColumnFamilyId, mutation.Sequence);
            var inPending = GetOrCreate(pending, identity);
            if (inPending.ContainsKey(mutation.Key))
            {
                throw Duplicate(mutation);
            }

            if (_versions.TryGetValue(identity, out var recoveredKeys) &&
                recoveredKeys.TryGetValue(mutation.Key, out var earlier))
            {
                if (StringComparer.Ordinal.Equals(earlier.Source, source) ||
                    !SameContent(earlier.Mutation, mutation))
                {
                    throw Duplicate(mutation);
                }

                _ = skipped.Add(mutation);
                continue;
            }

            inPending.Add(mutation.Key, new Recovered(source, mutation));
        }

        foreach (var (identity, keys) in pending)
        {
            var recoveredKeys = GetOrCreate(_versions, identity);
            foreach (var (key, recovered) in keys)
            {
                recoveredKeys.Add(key.ToArray(), recovered);
            }
        }

        return skipped.Count == 0
            ? mutations
            : [.. mutations.Where(mutation => !skipped.Contains(mutation))];
    }

    static StorageException Duplicate(WalMutation mutation) => new(
        $"WAL contains a duplicate logical key version for column family " +
        $"{mutation.ColumnFamilyId} at sequence {mutation.Sequence}.");

    static bool SameContent(WalMutation left, WalMutation right) =>
        left.Operation == right.Operation &&
        left.Expiration == right.Expiration &&
        (left.Value ?? []).AsSpan().SequenceEqual(right.Value ?? []) &&
        (left.RangeEnd ?? []).AsSpan().SequenceEqual(right.RangeEnd ?? []) &&
        (left.Value is null) == (right.Value is null);

    static Dictionary<byte[], Recovered> GetOrCreate(
        Dictionary<(uint ColumnFamilyId, ulong Sequence), Dictionary<byte[], Recovered>> versions,
        (uint ColumnFamilyId, ulong Sequence) identity)
    {
        if (!versions.TryGetValue(identity, out var keys))
        {
            keys = new Dictionary<byte[], Recovered>(ByteArrayComparer.Instance);
            versions.Add(identity, keys);
        }

        return keys;
    }

    static bool IsPointMutation(WalOperation operation) =>
        operation is WalOperation.Put or
            WalOperation.Insert or
            WalOperation.Delete;

    sealed record Recovered(string Source, WalMutation Mutation);
}
