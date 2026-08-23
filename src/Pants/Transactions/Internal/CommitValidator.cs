using System.Collections.Immutable;

namespace Cntryl.Pants.Transactions.Internal;

static class CommitValidator
{
    public static void Validate(RuntimeState state, CommitPayload payload)
    {
        foreach (var (identity, assertions) in payload.Asserts)
        {
            ValidateActiveFamily(state, identity);
            var startFamily = payload.StartSnapshot.Families[identity];
            var currentFamily = GetFamily(state, identity);
            foreach (var assertion in assertions)
            {
                var key = assertion.Key;
                var start = ResolveVisibleCell(startFamily, key, payload.SnapshotTime);
                if (!Matches(assertion.Expected, start, payload.SnapshotTime))
                {
                    throw PointConflict(
                        "A value assertion did not match the transaction's start snapshot.");
                }

                if (currentFamily.TryGetValue(key, out var current) &&
                    current.WriteSequence > payload.StartSnapshot.Sequence)
                {
                    throw PointConflict("An asserted key changed after the transaction began.");
                }

                if (state.RangeTombstones[identity].Any(tombstone =>
                        tombstone.WriteSequence > payload.StartSnapshot.Sequence &&
                        IsInRange(key, tombstone.Start, tombstone.EndExclusive)))
                {
                    throw PointConflict(
                        "An asserted key was covered by a range deletion after the transaction began.");
                }
            }
        }

        var now = state.Clock.UtcNow;
        payload.Operations.ForEach(operation =>
        {
            ValidateActiveFamily(state, operation.Family);
            if (payload.ConflictPolicy == PantsConflictPolicy.AbortOnWriteConflict)
            {
                ValidateWriteConflict(state, payload, operation);
            }

            if (operation.Kind == CommitOperationKind.Put &&
                operation.InsertOnly &&
                ResolvePriorExists(state, payload, operation, now))
            {
                throw PantsException.InvalidArgument("Insert requires an absent key.");
            }
        });
    }

    static void ValidateWriteConflict(
        RuntimeState state,
        CommitPayload payload,
        TransactionIntentOperation operation)
    {
        var family = GetFamily(state, operation.Family);
        switch (operation.Kind)
        {
            case CommitOperationKind.Put:
            case CommitOperationKind.Delete:
                if (family.TryGetValue(operation.Key, out var current) &&
                    current.WriteSequence > payload.StartSnapshot.Sequence)
                {
                    throw PointConflict("A write-set key changed after the transaction began.");
                }

                if (state.RangeTombstones[operation.Family].Any(tombstone =>
                        tombstone.WriteSequence > payload.StartSnapshot.Sequence &&
                        IsInRange(operation.Key, tombstone.Start, tombstone.EndExclusive)))
                {
                    throw PointConflict("A recent range deletion covers a write-set key.");
                }

                break;
            case CommitOperationKind.DeleteRange when operation.EndExclusive is not null:
                if (family.Any(pair =>
                        IsInRange(pair.Key, operation.Key, operation.EndExclusive) &&
                        pair.Value.WriteSequence > payload.StartSnapshot.Sequence))
                {
                    throw RangeConflict("A covered range changed after the transaction began.");
                }

                if (state.RangeTombstones[operation.Family].Any(tombstone =>
                        tombstone.WriteSequence > payload.StartSnapshot.Sequence &&
                        RangesOverlap(
                            operation.Key,
                            operation.EndExclusive,
                            tombstone.Start,
                            tombstone.EndExclusive)))
                {
                    throw RangeConflict(
                        "A covered range was deleted after the transaction began.");
                }

                break;
        }
    }

    static bool ResolvePriorExists(
        RuntimeState state,
        CommitPayload payload,
        TransactionIntentOperation operation,
        DateTimeOffset now)
    {
        var prior = payload.Operations.LatestBefore(operation.Ordinal, operation.Key);
        return prior switch
        {
            { IsDeleted: true } => false,
            { IsDeleted: false } => true,
            _ => ResolveVisibleCell(
                GetFamily(state, operation.Family),
                operation.Key,
                now)?.Value is not null
        };
    }

    static void ValidateActiveFamily(
        RuntimeState state,
        ColumnFamilyIdentity identity)
    {
        if (!state.ActiveFamilyVersions.TryGetValue(identity.Name, out var generation) ||
            generation != identity.Generation ||
            !state.FamilyData.ContainsKey(identity))
        {
            throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Column-family handle '{identity.Name}#{identity.Id}' is stale.");
        }
    }

    static ImmutableSortedDictionary<byte[], CellState> GetFamily(
        RuntimeState state,
        ColumnFamilyIdentity identity) =>
        state.FamilyData.TryGetValue(identity, out var family)
            ? family
            : throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Column family '{identity.Name}' is unavailable.");

    static CellState? ResolveVisibleCell(
        ImmutableSortedDictionary<byte[], CellState> family,
        byte[] key,
        DateTimeOffset now) =>
        family.TryGetValue(key, out var cell) && !cell.IsExpired(now) && cell.Value is not null
            ? cell
            : null;

    static bool Matches(
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

    static bool IsInRange(byte[] key, byte[] start, byte[] end) =>
        ByteArrayComparer.Instance.Compare(key, start) >= 0 &&
        ByteArrayComparer.Instance.Compare(key, end) < 0;

    static bool RangesOverlap(
        byte[] leftStart,
        byte[] leftEnd,
        byte[] rightStart,
        byte[] rightEnd) =>
        ByteArrayComparer.Instance.Compare(leftStart, rightEnd) < 0 &&
        ByteArrayComparer.Instance.Compare(rightStart, leftEnd) < 0;

    static PantsWriteConflictException PointConflict(string message) =>
        new(message, false);

    static PantsWriteConflictException RangeConflict(string message) =>
        new(message, true);
}
