using System.Collections.Immutable;

namespace Cntryl.Pants.Runtime.Internal.Transactions;

/// <summary>
///     Builds replacement immutable roots without changing the visible runtime. The owner publishes
///     one complete batch after every operation and its memory accounting have succeeded.
/// </summary>
sealed class StagedMemtableApply(RuntimeState state)
{
    readonly Dictionary<ColumnFamilyIdentity, FamilyApply> _families = new(ColumnFamilyIdentityComparer.Instance);

    public IReadOnlyList<ColumnFamilyIdentity> Families => _families.Keys.ToArray();

    public void Stage(TransactionIntentOperation operation, ulong sequence)
    {
        if (!_families.TryGetValue(operation.Family, out var staged))
        {
            if (!state.FamilyData.TryGetValue(operation.Family, out var cells))
            {
                throw PantsException.InvalidArgument($"Column family '{operation.Family.Name}' is unavailable.");
            }

            staged = new FamilyApply(cells, state.RangeTombstones[operation.Family],
                state.ActiveMemtableBytes[operation.Family]);
            _families.Add(operation.Family, staged);
        }

        switch (operation.Kind)
        {
            case CommitOperationKind.Put:
                staged.Cells = staged.Cells.SetItem(operation.Key.ToArray(),
                    CellState.FromUnixMilliseconds(operation.Value?.ToArray(), sequence,
                        operation.ExpirationUnixMilliseconds));
                break;
            case CommitOperationKind.Delete:
                staged.Cells = staged.Cells.SetItem(operation.Key.ToArray(), new CellState(null, sequence, null));
                break;
            case CommitOperationKind.DeleteRange when operation.EndExclusive is not null:
                staged.Ranges = staged.Ranges.Add(new CommittedRangeTombstone(
                    operation.Key.ToArray(), operation.EndExclusive.ToArray(), sequence));
                foreach (var key in staged.Cells.Keys)
                {
                    if (ByteArrayComparer.Instance.Compare(key, operation.Key) >= 0 &&
                        ByteArrayComparer.Instance.Compare(key, operation.EndExclusive) < 0)
                    {
                        staged.Cells = staged.Cells.SetItem(key, new CellState(null, sequence, null));
                    }
                }

                break;
            default:
                throw new PantsInternalException($"Unsupported transaction operation '{operation.Kind}'.");
        }

        staged.MemtableBytes = checked(staged.MemtableBytes +
                                      CoalescedCommitApplyPreflight.EstimateOperationBytes(operation));
    }

    public void Publish(ulong sequence)
    {
        foreach (var (family, staged) in _families)
        {
            state.FamilyData[family] = staged.Cells;
            state.RangeTombstones[family] = staged.Ranges;
            state.ActiveMemtableBytes[family] = staged.MemtableBytes;
            state.UnflushedFamilies.Add(family);
        }

        state.Sequence = sequence;
    }

    sealed class FamilyApply(
        ImmutableSortedDictionary<byte[], CellState> cells,
        ImmutableArray<CommittedRangeTombstone> ranges,
        long memtableBytes)
    {
        public ImmutableSortedDictionary<byte[], CellState> Cells { get; set; } = cells;

        public ImmutableArray<CommittedRangeTombstone> Ranges { get; set; } = ranges;

        public long MemtableBytes { get; set; } = memtableBytes;
    }
}
