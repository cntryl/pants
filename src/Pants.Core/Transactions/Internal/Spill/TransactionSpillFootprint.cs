namespace Cntryl.Pants.Transactions.Internal.Spill;

/// <summary>
///     Computes the on-disk size of a spill run file and its range index for a given sorted
///     operation list, so the local-storage ledger can charge the bytes before they are written.
/// </summary>
/// <remarks>
///     The figures mirror the layouts written by <see cref="TransactionSpillStore" /> and
///     <see cref="TransactionSpillRangeIndex" />. Keys are duplicated on disk: each operation's key
///     appears in its frame, every sixteenth key in the sparse index, and every delete-range's start
///     and end appear again in its range-index node. Charging only the operations under-reserves,
///     which lets a spill be admitted that then overruns the budget.
/// </remarks>
static class TransactionSpillFootprint
{
    /// <summary>Run file header.</summary>
    const long RunHeaderBytes = 48;

    /// <summary>Frame header (length and CRC) plus ordinal, kind, family, TTL flag, TTL and two length prefixes.</summary>
    const long OperationFrameFixedBytes = 8 + 30;

    /// <summary>Frame header plus an ordinal-table entry of ordinal and offset.</summary>
    const long OrdinalTableEntryBytes = 8 + 16;

    /// <summary>Frame header plus a sparse-index key length and file offset.</summary>
    const long SparseIndexFixedBytes = 8 + 4 + 8;

    /// <summary>Range-index file header.</summary>
    const long RangeIndexHeaderBytes = 32;

    /// <summary>Offset-table entry of a range-index node.</summary>
    const long RangeIndexTableEntryBytes = 12;

    /// <summary>Frame header plus the four node fields and two length prefixes of a range-index node.</summary>
    const long RangeIndexNodeFixedBytes = 8 + 32 + 4 + 4;

    /// <summary>Must match <c>TransactionSpillStore</c>'s sparse index stride.</summary>
    const int SparseIndexStride = TransactionSpillStore.SparseIndexStride;

    /// <summary>
    ///     Bytes a run of <paramref name="sortedOperations" /> occupies across its run file and its
    ///     range index, sorted in the order the store writes them.
    /// </summary>
    public static long Measure(IReadOnlyList<TransactionIntentOperation> sortedOperations)
    {
        ArgumentNullException.ThrowIfNull(sortedOperations);

        long runBytes = RunHeaderBytes;
        long rangeBytes = RangeIndexHeaderBytes;
        for (var index = 0; index < sortedOperations.Count; index++)
        {
            var operation = sortedOperations[index];
            var keyBytes = (long)operation.Key.Length;
            var secondBytes = SecondFieldLength(operation);

            runBytes = checked(
                runBytes +
                OperationFrameFixedBytes +
                keyBytes +
                secondBytes +
                OrdinalTableEntryBytes);

            if (index % SparseIndexStride == 0)
            {
                runBytes = checked(runBytes + SparseIndexFixedBytes + keyBytes);
            }

            if (operation.Kind == CommitOperationKind.DeleteRange)
            {
                rangeBytes = checked(
                    rangeBytes +
                    RangeIndexNodeFixedBytes +
                    RangeIndexTableEntryBytes +
                    keyBytes +
                    secondBytes);
            }
        }

        return checked(runBytes + rangeBytes);
    }

    static long SecondFieldLength(TransactionIntentOperation operation) => operation.Kind switch
    {
        CommitOperationKind.Put => operation.Value?.Length ?? 0,
        CommitOperationKind.DeleteRange => operation.EndExclusive?.Length ?? 0,
        _ => 0
    };
}
