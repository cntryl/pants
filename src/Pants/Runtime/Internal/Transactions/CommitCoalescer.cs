namespace Pants;

internal sealed class CommitCoalescer(
    bool enabled,
    long memtableSizeLimitBytes,
    RuntimeTelemetry telemetry,
    Func<
        IReadOnlyList<WalCommitGroupEntry>,
        PantsRuntimeState,
        PantsDurability,
        PantsFailpoint,
        ValueTask<WalCommitGroupResult>> appendGroupAsync,
    Action<PantsRuntimeState, TransactionIntentOperation, long> applyOperation)
{
    public bool CanAttempt(IReadOnlyCollection<CommitRuntimeCommand> commits) =>
        enabled && commits.Count > 1;

    public bool TryStage(
        PantsRuntimeState state,
        CommitRuntimeCommand command,
        PantsDurability? groupDurability,
        Dictionary<ColumnFamilyIdentity, long> stagedBytesByFamily)
    {
        var durability = command.WriteOptions.Durability;
        if (durability is not (PantsDurability.Sync or PantsDurability.Buffered) ||
            (groupDurability is { } expectedDurability && durability != expectedDurability) ||
            command.Payload.Operations.Count == 0 ||
            command.Payload.IsSpilled ||
            command.Payload.ConflictPolicy != PantsConflictPolicy.LastWriteWins ||
            command.Payload.Asserts.Values.Any(static assertions => assertions.Count != 0))
        {
            return false;
        }

        var commandBytesByFamily = new Dictionary<ColumnFamilyIdentity, long>(
            ColumnFamilyIdentityComparer.Instance);
        try
        {
            var containsInsert = false;
            command.Payload.Operations.ForEach(operation =>
            {
                containsInsert |= operation.InsertOnly;
                commandBytesByFamily[operation.Family] = checked(
                    commandBytesByFamily.GetValueOrDefault(operation.Family) +
                    CoalescedCommitApplyPreflight.EstimateOperationBytes(operation));
            });
            if (containsInsert)
            {
                return false;
            }

            foreach (var pair in commandBytesByFamily)
            {
                var stagedBytes = checked(
                    stagedBytesByFamily.GetValueOrDefault(pair.Key) + pair.Value);
                var activeBytes = state.ActiveMemtableBytes.GetValueOrDefault(pair.Key);
                if (activeBytes != 0 &&
                    checked(activeBytes + stagedBytes) > memtableSizeLimitBytes)
                {
                    return false;
                }
            }

            foreach (var pair in commandBytesByFamily)
            {
                stagedBytesByFamily[pair.Key] = checked(
                    stagedBytesByFamily.GetValueOrDefault(pair.Key) + pair.Value);
            }

            return true;
        }
        catch (Exception exception) when (exception is OverflowException or PantsException)
        {
            return false;
        }
    }

    public static IReadOnlyList<PreparedCoalescedCommit> CreatePreparedCommits(
        PantsRuntimeState state,
        IReadOnlyList<CommitRuntimeCommand> commands) =>
        CoalescedCommitApplyPreflight.Create(state, commands);

    public async ValueTask AppendAsync(
        PantsRuntimeState state,
        IReadOnlyList<PreparedCoalescedCommit> prepared,
        PantsDurability durability,
        PantsFailpoint beforeSync)
    {
        if (prepared.Count == 0 ||
            durability is not (PantsDurability.Sync or PantsDurability.Buffered) ||
            prepared.Any(commit => commit.Command.WriteOptions.Durability != durability))
        {
            throw new PantsInternalException(
                "A coalesced WAL group must use one supported durability policy.");
        }

        var group = prepared
            .Select(static commit => new WalCommitGroupEntry(
                commit.Command.Payload,
                commit.Sequence))
            .ToArray();
        var result = await appendGroupAsync(group, state, durability, beforeSync)
            .ConfigureAwait(false);
        if (durability == PantsDurability.Sync)
        {
            telemetry.RecordDurabilityWaitersFannedOut(result.CommitCount);
        }
    }

    public void Apply(
        PantsRuntimeState state,
        IReadOnlyList<PreparedCoalescedCommit> prepared)
    {
        foreach (var commit in prepared)
        {
            foreach (var operation in commit.Operations)
            {
                applyOperation(state, operation, commit.Sequence);
            }

            foreach (var pair in commit.BytesByFamily)
            {
                state.ActiveMemtableBytes[pair.Key] = checked(
                    state.ActiveMemtableBytes[pair.Key] + pair.Value);
            }

            foreach (var family in commit.Families)
            {
                state.UnflushedFamilies.Add(family);
            }
        }
    }
}
