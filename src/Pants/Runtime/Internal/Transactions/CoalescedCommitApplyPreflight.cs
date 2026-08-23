namespace Cntryl.Pants;

internal static class CoalescedCommitApplyPreflight
{
    public static IReadOnlyList<PreparedCoalescedCommit> Create(
        PantsRuntimeState state,
        IReadOnlyList<CommitRuntimeCommand> commands)
    {
        var prepared = new List<PreparedCoalescedCommit>(commands.Count);
        var totalBytesByFamily = new Dictionary<ColumnFamilyIdentity, long>(
            ColumnFamilyIdentityComparer.Instance);
        var sequence = state.Sequence;
        foreach (var command in commands)
        {
            sequence = GetCommitSequence(sequence, command.Payload.Operations.Count);
            var operations = new List<TransactionIntentOperation>();
            var bytesByFamily = new Dictionary<ColumnFamilyIdentity, long>(
                ColumnFamilyIdentityComparer.Instance);
            var families = new HashSet<ColumnFamilyIdentity>(
                ColumnFamilyIdentityComparer.Instance);
            try
            {
                command.Payload.Operations.ForEach(operation =>
                {
                    ValidateOperation(state, operation);
                    operations.Add(operation);
                    families.Add(operation.Family);
                    var bytes = EstimateOperationBytes(operation);
                    bytesByFamily[operation.Family] = checked(
                        bytesByFamily.GetValueOrDefault(operation.Family) + bytes);
                    totalBytesByFamily[operation.Family] = checked(
                        totalBytesByFamily.GetValueOrDefault(operation.Family) + bytes);
                });
            }
            catch (OverflowException exception)
            {
                throw new PantsInternalException(
                    "Coalesced transaction memory accounting overflowed during preflight.",
                    exception);
            }

            prepared.Add(new PreparedCoalescedCommit(
                command,
                sequence,
                operations,
                bytesByFamily,
                [.. families]));
        }

        foreach (var pair in totalBytesByFamily)
        {
            try
            {
                _ = checked(state.ActiveMemtableBytes[pair.Key] + pair.Value);
            }
            catch (OverflowException exception)
            {
                throw new PantsInternalException(
                    "Coalesced transaction memory accounting overflowed during apply preflight.",
                    exception);
            }
        }

        return prepared;
    }

    public static long EstimateOperationBytes(TransactionIntentOperation operation) => checked(
        (long)operation.Key.Length +
        (operation.EndExclusive?.Length ?? 0) +
        (operation.Value?.Length ?? 0) +
        64);

    static long GetCommitSequence(long sequence, ulong operationCount)
    {
        try
        {
            return checked(sequence + checked((long)operationCount) + 2);
        }
        catch (OverflowException exception)
        {
            throw new PantsStorageException(
                "The transaction sequence range is exhausted.",
                exception);
        }
    }

    static void ValidateOperation(
        PantsRuntimeState state,
        TransactionIntentOperation operation)
    {
        if (!state.ActiveFamilyVersions.TryGetValue(
                operation.Family.Name,
                out var activeGeneration) ||
            activeGeneration != operation.Family.Generation ||
            !state.FamilyData.ContainsKey(operation.Family) ||
            !state.RangeTombstones.ContainsKey(operation.Family) ||
            !state.ActiveMemtableBytes.ContainsKey(operation.Family))
        {
            throw PantsException.Create(
                PantsErrorCode.InvalidArgument,
                $"Column-family handle '{operation.Family.Name}#{operation.Family.Id}' is stale.");
        }

        if (operation.Kind is CommitOperationKind.Put or CommitOperationKind.Delete)
        {
            return;
        }

        if (operation.Kind == CommitOperationKind.DeleteRange &&
            operation.EndExclusive is not null)
        {
            return;
        }

        throw new PantsInternalException(
            $"Unsupported transaction operation '{operation.Kind}'.");
    }
}
