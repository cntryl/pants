namespace Pants;

internal static class CommitValidator
{
    public static void Validate(PantsRuntimeState state, CommitPayload payload)
    {
        DateTimeOffset now = state.Clock.UtcNow;
        ValidateInsertOnlyOperations(state, payload, now);
        foreach ((ColumnFamilyIdentity identity, IReadOnlyList<TransactionAssertion> assertions) in payload.Asserts)
        {
            ValidateActiveFamily(state, identity);
            SortedDictionary<byte[], CellState> startFamily = payload.StartSnapshot.Families[identity];
            SortedDictionary<byte[], CellState> currentFamily = GetFamily(state, identity);
            foreach (TransactionAssertion assertion in assertions)
            {
                byte[] key = assertion.Key;
                CellState? start = ResolveVisibleCell(startFamily, key, payload.SnapshotTime);
                if (!Matches(assertion.Expected, start, payload.SnapshotTime))
                {
                    throw PantsException.Create(
                        PantsErrorCode.WriteConflict,
                        "A value assertion did not match the transaction's start snapshot.");
                }

                if (currentFamily.TryGetValue(key, out CellState? current) &&
                    current.WriteSequence > payload.StartSnapshot.Sequence)
                {
                    throw PantsException.Create(
                        PantsErrorCode.WriteConflict,
                        "An asserted key changed after the transaction began.");
                }

                if (state.RangeTombstones[identity].Any(tombstone =>
                        tombstone.WriteSequence > payload.StartSnapshot.Sequence &&
                        IsInRange(key, tombstone.Start, tombstone.EndExclusive)))
                {
                    throw PantsException.Create(
                        PantsErrorCode.WriteConflict,
                        "An asserted key was covered by a range deletion after the transaction began.");
                }
            }
        }

        foreach ((ColumnFamilyIdentity identity, Dictionary<byte[], TransactionPendingWrite> writes) in payload.Writes)
        {
            ValidateActiveFamily(state, identity);
            SortedDictionary<byte[], CellState> family = GetFamily(state, identity);
            foreach (byte[] key in writes.Keys)
            {
                if (payload.ConflictPolicy == PantsConflictPolicy.AbortOnWriteConflict &&
                    family.TryGetValue(key, out CellState? current) &&
                    current.WriteSequence > payload.StartSnapshot.Sequence)
                {
                    throw PantsException.Create(
                        PantsErrorCode.WriteConflict,
                        "A write-set key changed after the transaction began.");
                }

                if (payload.ConflictPolicy == PantsConflictPolicy.AbortOnWriteConflict &&
                    state.RangeTombstones[identity].Any(tombstone =>
                        tombstone.WriteSequence > payload.StartSnapshot.Sequence &&
                        IsInRange(key, tombstone.Start, tombstone.EndExclusive)))
                {
                    throw PantsException.Create(
                        PantsErrorCode.WriteConflict,
                        "A recent range deletion covers a write-set key.");
                }
            }
        }

        if (payload.ConflictPolicy != PantsConflictPolicy.AbortOnWriteConflict)
        {
            return;
        }

        foreach ((ColumnFamilyIdentity identity, List<DeleteRange> ranges) in payload.DeleteRanges)
        {
            ValidateActiveFamily(state, identity);
            SortedDictionary<byte[], CellState> family = GetFamily(state, identity);
            foreach (DeleteRange range in ranges)
            {
                if (family.Any(pair =>
                        IsInRange(pair.Key, range.Start, range.EndExclusive) &&
                        pair.Value.WriteSequence > payload.StartSnapshot.Sequence))
                {
                    throw PantsException.Create(
                        PantsErrorCode.WriteConflict,
                        "A covered range changed after the transaction began.");
                }

                if (state.RangeTombstones[identity].Any(tombstone =>
                        tombstone.WriteSequence > payload.StartSnapshot.Sequence &&
                        RangesOverlap(
                            range.Start,
                            range.EndExclusive,
                            tombstone.Start,
                            tombstone.EndExclusive)))
                {
                    throw PantsException.Create(
                        PantsErrorCode.WriteConflict,
                        "A covered range was deleted after the transaction began.");
                }
            }
        }
    }

    public static bool HasRangeConflict(PantsRuntimeState state, CommitPayload payload)
    {
        foreach ((ColumnFamilyIdentity identity, List<DeleteRange> ranges) in payload.DeleteRanges)
        {
            if (ranges.Count == 0)
            {
                continue;
            }

            SortedDictionary<byte[], CellState> family = GetFamily(state, identity);
            if (ranges.Any(range => family.Any(pair =>
                    pair.Value.WriteSequence > payload.StartSnapshot.Sequence &&
                    IsInRange(pair.Key, range.Start, range.EndExclusive))))
            {
                return true;
            }
        }

        foreach ((ColumnFamilyIdentity identity, IReadOnlyList<CommittedRangeTombstone> tombstones) in
                 state.RangeTombstones)
        {
            IEnumerable<byte[]> keys = payload.Writes.TryGetValue(identity, out var writes)
                ? writes.Keys
                : [];
            if (tombstones.Any(tombstone =>
                    tombstone.WriteSequence > payload.StartSnapshot.Sequence &&
                    keys.Any(key => IsInRange(key, tombstone.Start, tombstone.EndExclusive))))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateInsertOnlyOperations(
        PantsRuntimeState state,
        CommitPayload payload,
        DateTimeOffset now)
    {
        foreach (IGrouping<ColumnFamilyIdentity, TransactionIntentOperation> familyOperations in
                 payload.OrderedOperations.GroupBy(
                     static operation => operation.Family,
                     ColumnFamilyIdentityComparer.Instance))
        {
            ColumnFamilyIdentity identity = familyOperations.Key;
            ValidateActiveFamily(state, identity);
            SortedDictionary<byte[], CellState> family = GetFamily(state, identity);
            var pointStates = new Dictionary<byte[], (ulong Ordinal, bool Exists)>(
                ByteArrayComparer.Instance);
            var rangeDeletes = new List<(ulong Ordinal, byte[] Start, byte[] EndExclusive)>();
            foreach (TransactionIntentOperation operation in familyOperations)
            {
                switch (operation.Kind)
                {
                    case CommitOperationKind.Put:
                        if (operation.InsertOnly && ResolvePriorExists(
                                operation.Key,
                                pointStates,
                                rangeDeletes,
                                family,
                                now))
                        {
                            throw PantsException.Create(
                                PantsErrorCode.WriteConflict,
                                "Insert requires an absent key.");
                        }

                        pointStates[operation.Key] = (operation.Ordinal, true);
                        break;
                    case CommitOperationKind.Delete:
                        pointStates[operation.Key] = (operation.Ordinal, false);
                        break;
                    case CommitOperationKind.DeleteRange when operation.EndExclusive is not null:
                        rangeDeletes.Add((operation.Ordinal, operation.Key, operation.EndExclusive));
                        break;
                }
            }
        }
    }

    private static bool ResolvePriorExists(
        byte[] key,
        Dictionary<byte[], (ulong Ordinal, bool Exists)> pointStates,
        List<(ulong Ordinal, byte[] Start, byte[] EndExclusive)> rangeDeletes,
        SortedDictionary<byte[], CellState> family,
        DateTimeOffset now)
    {
        bool hasPrior = pointStates.TryGetValue(key, out (ulong Ordinal, bool Exists) pointState);
        ulong latestOrdinal = hasPrior ? pointState.Ordinal : 0;
        bool exists = hasPrior && pointState.Exists;
        foreach ((ulong ordinal, byte[] start, byte[] endExclusive) in rangeDeletes)
        {
            if (IsInRange(key, start, endExclusive) && (!hasPrior || ordinal > latestOrdinal))
            {
                hasPrior = true;
                latestOrdinal = ordinal;
                exists = false;
            }
        }

        return hasPrior
            ? exists
            : ResolveVisibleCell(family, key, now)?.Value is not null;
    }

    private static void ValidateActiveFamily(
        PantsRuntimeState state,
        ColumnFamilyIdentity identity)
    {
        if (!state.ActiveFamilyVersions.TryGetValue(identity.Name, out int generation) ||
            generation != identity.Generation ||
            !state.FamilyData.ContainsKey(identity))
        {
            throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Column-family handle '{identity.Name}#{identity.Id}' is stale.");
        }
    }

    private static SortedDictionary<byte[], CellState> GetFamily(
        PantsRuntimeState state,
        ColumnFamilyIdentity identity) =>
        state.FamilyData.TryGetValue(identity, out SortedDictionary<byte[], CellState>? family)
            ? family
            : throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Column family '{identity.Name}' is unavailable.");

    private static CellState? ResolveVisibleCell(
        SortedDictionary<byte[], CellState> family,
        byte[] key,
        DateTimeOffset now) =>
        family.TryGetValue(key, out CellState? cell) && !cell.IsExpired(now) && cell.Value is not null
            ? cell
            : null;

    private static bool Matches(
        TransactionReadValue expected,
        CellState? actual,
        DateTimeOffset now)
    {
        if (expected.Missing)
        {
            return actual is null || actual.Value is null || actual.IsExpired(now);
        }

        return actual?.Value is { } value && value.AsSpan().SequenceEqual(expected.Value);
    }

    private static bool IsInRange(byte[] key, byte[] start, byte[] end) =>
        ByteArrayComparer.Instance.Compare(key, start) >= 0 &&
        ByteArrayComparer.Instance.Compare(key, end) < 0;

    private static bool RangesOverlap(
        byte[] leftStart,
        byte[] leftEnd,
        byte[] rightStart,
        byte[] rightEnd) =>
        ByteArrayComparer.Instance.Compare(leftStart, rightEnd) < 0 &&
        ByteArrayComparer.Instance.Compare(rightStart, leftEnd) < 0;
}
