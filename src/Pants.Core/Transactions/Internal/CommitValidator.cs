using System.Collections.Immutable;

namespace Cntryl.Pants.Transactions.Internal;

static class CommitValidator
{
    public static void Validate(
        RuntimeState state,
        CommitPayload payload,
        IStorageReadStore? diskStore = null) =>
        ValidateAsync(state, payload, diskStore, null, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

    public static async ValueTask ValidateAsync(
        RuntimeState state,
        CommitPayload payload,
        IStorageReadStore? diskStore,
        ResourceBudget? scanMemoryBudget,
        CancellationToken cancellationToken)
    {
        foreach (var (identity, assertions) in payload.Asserts)
        {
            ValidateActiveFamily(state, identity);
            var startFamily = payload.StartSnapshot.Families[identity];
            var currentFamily = GetFamily(state, identity);
            foreach (var assertion in assertions)
            {
                var key = assertion.Key;
                var start = await ResolveStartCellAsync(
                    payload.StartSnapshot,
                    identity,
                    startFamily,
                    key,
                    diskStore,
                    cancellationToken).ConfigureAwait(false);
                if (!Matches(assertion.Expected, start, payload.SnapshotTime))
                {
                    throw PointConflict(
                        "A value assertion did not match the transaction's start snapshot.");
                }

                if ((currentFamily.TryGetValue(key, out var current) &&
                     current.WriteSequence > payload.StartSnapshot.Sequence) ||
                    (current is null &&
                     await ResolveDiskWriteSequenceAsync(
                         diskStore,
                         identity,
                         key,
                         cancellationToken).ConfigureAwait(false) is { } diskSequence &&
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
        await payload.Operations.ForEachAsync(async operation =>
        {
            ValidateActiveFamily(state, operation.Family);
            if (operation.Kind == CommitOperationKind.Put &&
                operation.InsertOnly &&
                await ResolvePriorExistsAsync(
                    state,
                    payload,
                    operation,
                    now,
                    diskStore,
                    cancellationToken).ConfigureAwait(false))
            {
                throw PantsException.InvalidArgument("Insert requires an absent key.");
            }

            if (payload.ConflictPolicy == PantsConflictPolicy.AbortOnWriteConflict)
            {
                await ValidateWriteConflictAsync(
                    state,
                    payload,
                    operation,
                    diskStore,
                    scanMemoryBudget,
                    cancellationToken).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    static async ValueTask ValidateWriteConflictAsync(
        RuntimeState state,
        CommitPayload payload,
        TransactionIntentOperation operation,
        IStorageReadStore? diskStore,
        ResourceBudget? scanMemoryBudget,
        CancellationToken cancellationToken)
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
                    await ResolveDiskWriteSequenceAsync(
                            diskStore,
                            operation.Family,
                            operation.Key,
                            cancellationToken).ConfigureAwait(false) is
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

                if (await HasDiskMutationInRangeAsync(
                        diskStore,
                        operation.Family,
                        operation.Key,
                        operation.EndExclusive,
                        payload.StartSnapshot.Sequence,
                        scanMemoryBudget,
                        cancellationToken).ConfigureAwait(false))
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

    static async ValueTask<bool> ResolvePriorExistsAsync(
        RuntimeState state,
        CommitPayload payload,
        TransactionIntentOperation operation,
        DateTimeOffset now,
        IStorageReadStore? diskStore,
        CancellationToken cancellationToken)
    {
        var prior = payload.Operations.LatestBefore(operation.Ordinal, operation.Key);
        if (prior is { IsDeleted: true })
        {
            return false;
        }

        if (prior is { IsDeleted: false })
        {
            return true;
        }

        return await ResolveCurrentExistsAsync(
            state,
            operation.Family,
            operation.Key,
            now,
            diskStore,
            cancellationToken).ConfigureAwait(false);
    }

    static async ValueTask<bool> ResolveCurrentExistsAsync(
        RuntimeState state,
        ColumnFamilyIdentity family,
        byte[] key,
        DateTimeOffset now,
        IStorageReadStore? diskStore,
        CancellationToken cancellationToken)
    {
        if (GetFamily(state, family).TryGetValue(key, out var current))
        {
            return current.Value is not null && !current.IsExpired(now);
        }

        var diskEntry = await ResolveDiskEntryAsync(
            diskStore,
            family,
            key,
            cancellationToken).ConfigureAwait(false);
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
    ///     Falls through to the current manifest once the in-memory tier no longer has the key — it
    ///     may have been written before this process started, or released from
    ///     <see cref="RuntimeState.FamilyData" /> after a flush durably published it (see
    ///     <see cref="RuntimeState.ReleaseFlushedGeneration" />). Returns the durable sequence at
    ///     which the key last changed (put or delete), or <c>null</c> if it has no durable record.
    /// </summary>
    static async ValueTask<long?> ResolveDiskWriteSequenceAsync(
        IStorageReadStore? diskStore,
        ColumnFamilyIdentity family,
        byte[] key,
        CancellationToken cancellationToken)
    {
        if (diskStore is null)
        {
            return null;
        }

        var sequence = await diskStore.GetLatestMutationSequenceAsync(
                GetDiskCandidates(diskStore, family, key),
                key,
                cancellationToken)
            .ConfigureAwait(false);
        return sequence is { } value ? checked((long)value) : null;
    }

    static async ValueTask<SstEntry?> ResolveDiskEntryAsync(
        IStorageReadStore? diskStore,
        ColumnFamilyIdentity family,
        byte[] key,
        CancellationToken cancellationToken)
    {
        if (diskStore is null)
        {
            return null;
        }

        var candidates = GetDiskCandidates(diskStore, family, key);
        return candidates.Length == 0
            ? null
            : await diskStore.TryReadPointValueAsync(candidates, key, cancellationToken)
                .ConfigureAwait(false);
    }

    static async ValueTask<SstEntry?> ResolveDiskCellEntryAsync(
        IStorageReadStore? diskStore,
        IReadOnlyList<FileMeta> visibleFiles,
        byte[] key,
        CancellationToken cancellationToken)
    {
        if (diskStore is null)
        {
            return null;
        }

        var candidates = visibleFiles
            .Where(file =>
                diskStore.IsSstAvailable(file) &&
                diskStore.IsWithinFileRange(file, key))
            .OrderByDescending(static file => file.SstSequence)
            .ToArray();
        return candidates.Length == 0
            ? null
            : await diskStore.TryReadPointValueAsync(candidates, key, cancellationToken)
                .ConfigureAwait(false);
    }

    static async ValueTask<CellState?> ResolveDiskCellAsync(
        IStorageReadStore? diskStore,
        IReadOnlyList<FileMeta> visibleFiles,
        byte[] key,
        CancellationToken cancellationToken)
    {
        var entry = await ResolveDiskCellEntryAsync(
            diskStore,
            visibleFiles,
            key,
            cancellationToken).ConfigureAwait(false);
        return entry is null
            ? null
            : CellState.FromUnixMilliseconds(
                entry.IsDelete ? null : entry.Value,
                checked((long)entry.Sequence),
                entry.Expiration);
    }

    static async ValueTask<CellState?> ResolveStartCellAsync(
        DatabaseVersion startSnapshot,
        ColumnFamilyIdentity family,
        ImmutableSortedDictionary<byte[], CellState> startFamily,
        byte[] key,
        IStorageReadStore? diskStore,
        CancellationToken cancellationToken)
    {
        if (startFamily.TryGetValue(key, out var startCell))
        {
            return startCell;
        }

        var diskCell = await ResolveDiskCellAsync(
            diskStore,
            startSnapshot.GetVisibleFiles(family.Id),
            key,
            cancellationToken).ConfigureAwait(false);
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
        IStorageReadStore diskStore,
        ColumnFamilyIdentity family,
        byte[] key) => diskStore.GetVisibleFilesSnapshot()
        .GetValueOrDefault(family.Id, [])
        .Where(file =>
            diskStore.IsSstAvailable(file) &&
            diskStore.IsWithinFileRange(file, key))
        .OrderByDescending(static file => file.SstSequence)
        .ToArray();

    static async ValueTask<bool> HasDiskMutationInRangeAsync(
        IStorageReadStore? diskStore,
        ColumnFamilyIdentity family,
        byte[] start,
        byte[] end,
        long afterSequence,
        ResourceBudget? scanMemoryBudget,
        CancellationToken cancellationToken)
    {
        if (diskStore is null)
        {
            return false;
        }

        var candidates = diskStore.GetVisibleFilesSnapshot()
            .GetValueOrDefault(family.Id, []);
        return await diskStore.HasMutationInRangeAsync(
                candidates,
                start,
                end,
                checked((ulong)afterSequence),
                scanMemoryBudget,
                cancellationToken)
            .ConfigureAwait(false);
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
