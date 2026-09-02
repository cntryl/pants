using System.Collections.Immutable;

namespace Cntryl.Pants.Transactions.Internal;

static class CommitValidator
{
    public static void Validate(RuntimeState state, CommitPayload payload, LocalDiskStore? diskStore = null)
    {
        foreach (var (identity, assertions) in payload.Asserts)
        {
            ValidateActiveFamily(state, identity);
            var startFamily = payload.StartSnapshot.Families[identity];
            var currentFamily = GetFamily(state, identity);
            foreach (var assertion in assertions)
            {
                var key = assertion.Key;
                var start = ResolveStartCell(
                    payload.StartSnapshot,
                    identity,
                    startFamily,
                    key,
                    diskStore);
                if (!Matches(assertion.Expected, start, payload.SnapshotTime))
                {
                    throw PointConflict(
                        "A value assertion did not match the transaction's start snapshot.");
                }

                if ((currentFamily.TryGetValue(key, out var current) &&
                     current.WriteSequence > payload.StartSnapshot.Sequence) ||
                    (current is null &&
                     ResolveDiskWriteSequence(diskStore, identity, key) is { } diskSequence &&
                     diskSequence > payload.StartSnapshot.Sequence))
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
            if (operation.Kind == CommitOperationKind.Put &&
                operation.InsertOnly &&
                ResolvePriorExists(state, payload, operation, now, diskStore))
            {
                throw PantsException.InvalidArgument("Insert requires an absent key.");
            }

            if (payload.ConflictPolicy == PantsConflictPolicy.AbortOnWriteConflict)
            {
                ValidateWriteConflict(state, payload, operation, diskStore);
            }
        });
    }

    static void ValidateWriteConflict(
        RuntimeState state,
        CommitPayload payload,
        TransactionIntentOperation operation,
        LocalDiskStore? diskStore)
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

                // The in-memory tier no longer has this key at all (e.g. its write was flushed
                // and released — RuntimeState.ReleaseFlushedGeneration — after this transaction
                // began but before it committed); the durable SST is the only remaining witness.
                if (current is null &&
                    ResolveDiskWriteSequence(diskStore, operation.Family, operation.Key) is
                    { } diskSequence &&
                    diskSequence > payload.StartSnapshot.Sequence)
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

                if (HasDiskMutationInRange(
                        diskStore,
                        operation.Family,
                        operation.Key,
                        operation.EndExclusive,
                        payload.StartSnapshot.Sequence))
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
            case CommitOperationKind.DeleteRange:
                throw new PantsInternalException(
                    "A range-delete operation requires an exclusive end key.");
            default:
                throw new PantsInternalException(
                    $"Unsupported commit operation kind '{operation.Kind}'.");
        }
    }

    static bool ResolvePriorExists(
        RuntimeState state,
        CommitPayload payload,
        TransactionIntentOperation operation,
        DateTimeOffset now,
        LocalDiskStore? diskStore)
    {
        var prior = payload.Operations.LatestBefore(operation.Ordinal, operation.Key);
        return prior switch
        {
            { IsDeleted: true } => false,
            { IsDeleted: false } => true,
            _ => ResolveCurrentExists(state, operation.Family, operation.Key, now, diskStore)
        };
    }

    static bool ResolveCurrentExists(
        RuntimeState state,
        ColumnFamilyIdentity family,
        byte[] key,
        DateTimeOffset now,
        LocalDiskStore? diskStore)
    {
        if (GetFamily(state, family).TryGetValue(key, out var current))
        {
            return current.Value is not null && !current.IsExpired(now);
        }

        var diskEntry = ResolveDiskEntry(diskStore, family, key);
        var currentRangeSequence = state.RangeTombstones[family]
            .Where(tombstone => IsInRange(key, tombstone.Start, tombstone.EndExclusive))
            .Select(static tombstone => (long?)tombstone.WriteSequence)
            .Max();
        if (currentRangeSequence is { } rangeSequence &&
            (diskEntry is null || rangeSequence > checked((long)diskEntry.Sequence)))
        {
            return false;
        }

        return diskEntry is { IsDelete: false } &&
               !UnixTimestamp.IsExpired(diskEntry.Expiration, now);
    }

    /// <summary>
    /// Falls through to the current manifest once the in-memory tier no longer has the key — it
    /// may have been written before this process started, or released from
    /// <see cref="RuntimeState.FamilyData"/> after a flush durably published it (see
    /// <see cref="RuntimeState.ReleaseFlushedGeneration"/>). Returns the durable sequence at
    /// which the key last changed (put or delete), or <c>null</c> if it has no durable record.
    /// </summary>
    static long? ResolveDiskWriteSequence(LocalDiskStore? diskStore, ColumnFamilyIdentity family, byte[] key) =>
        diskStore?.GetLatestMutationSequence(GetDiskCandidates(diskStore, family, key), key) is { } sequence
            ? checked((long)sequence)
            : null;

    static SstEntry? ResolveDiskEntry(LocalDiskStore? diskStore, ColumnFamilyIdentity family, byte[] key)
    {
        if (diskStore is null)
        {
            return null;
        }

        var candidates = GetDiskCandidates(diskStore, family, key);
        return candidates.Length == 0 ? null : diskStore.TryReadPointValue(candidates, key);
    }

    static SstEntry? ResolveDiskCellEntry(
        LocalDiskStore? diskStore,
        IReadOnlyList<FileMeta> visibleFiles,
        byte[] key)
    {
        if (diskStore is null)
        {
            return null;
        }

        var candidates = visibleFiles
            .Where(file =>
                diskStore.IsSstAvailable(file) &&
                LocalDiskStore.IsWithinFileRange(file, key))
            .OrderByDescending(static file => file.SstSequence)
            .ToArray();
        return candidates.Length == 0 ? null : diskStore.TryReadPointValue(candidates, key);
    }

    static CellState? ResolveDiskCell(
        LocalDiskStore? diskStore,
        IReadOnlyList<FileMeta> visibleFiles,
        byte[] key) => ResolveDiskCellEntry(diskStore, visibleFiles, key) is { } entry
        ? CellState.FromUnixMilliseconds(
            entry.IsDelete ? null : entry.Value,
            checked((long)entry.Sequence),
            entry.Expiration)
        : null;

    static CellState? ResolveStartCell(
        DatabaseVersion startSnapshot,
        ColumnFamilyIdentity family,
        ImmutableSortedDictionary<byte[], CellState> startFamily,
        byte[] key,
        LocalDiskStore? diskStore)
    {
        if (startFamily.TryGetValue(key, out var startCell))
        {
            return startCell;
        }

        var diskCell = ResolveDiskCell(
            diskStore,
            startSnapshot.GetVisibleFiles(family.Id),
            key);
        var coveringRangeSequence = startSnapshot.RangeTombstones[family]
            .Where(tombstone => IsInRange(key, tombstone.Start, tombstone.EndExclusive))
            .Select(static tombstone => (long?)tombstone.WriteSequence)
            .Max();
        return coveringRangeSequence is { } rangeSequence &&
               (diskCell is null || rangeSequence > diskCell.WriteSequence)
            ? new CellState(null, rangeSequence, null)
            : diskCell;
    }

    static FileMeta[] GetDiskCandidates(
        LocalDiskStore diskStore,
        ColumnFamilyIdentity family,
        byte[] key) => diskStore.GetVisibleFilesSnapshot()
        .GetValueOrDefault(family.Id, [])
        .Where(file =>
            diskStore.IsSstAvailable(file) &&
            LocalDiskStore.IsWithinFileRange(file, key))
        .OrderByDescending(static file => file.SstSequence)
        .ToArray();

    static bool HasDiskMutationInRange(
        LocalDiskStore? diskStore,
        ColumnFamilyIdentity family,
        byte[] start,
        byte[] end,
        long afterSequence)
    {
        if (diskStore is null)
        {
            return false;
        }

        var candidates = diskStore.GetVisibleFilesSnapshot()
            .GetValueOrDefault(family.Id, []);
        return diskStore.HasMutationInRange(
            candidates,
            start,
            end,
            checked((ulong)afterSequence));
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
