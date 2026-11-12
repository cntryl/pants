namespace Cntryl.Pants.Runtime.Internal.Transactions;

sealed class CommitCoalescer(
    bool enabled,
    long memtableSizeLimitBytes,
    RuntimeTelemetry telemetry,
    Func<
        IReadOnlyList<WalCommitGroupEntry>,
        ulong,
        PantsDurability,
        Failpoint,
        OperationDeadline,
        ValueTask<WalCommitGroupResult>> appendGroupAsync,
    Action afterOperationStaged)
{
    public bool CanAttempt(IReadOnlyCollection<CommitRuntimeCommand> commits) =>
        enabled && commits.Count > 1;

    public bool TryStage(
        RuntimeState state,
        CommitRuntimeCommand command,
        PantsDurability? groupDurability,
        Dictionary<ColumnFamilyIdentity, long> stagedBytesByFamily,
        IReadOnlyList<CommitRuntimeCommand>? stagedCommands = null)
    {
        var durability = command.WriteOptions.Durability;
        if (durability is not (PantsDurability.Sync or
                PantsDurability.Buffered or
                PantsDurability.BestEffort or
                PantsDurability.CloudAsync) ||
            (groupDurability is { } expectedDurability && durability != expectedDurability) ||
            command.Payload.Operations.Count == 0 ||
            command.Payload.IsSpilled)
        {
            return false;
        }

        var commandBytesByFamily = new Dictionary<ColumnFamilyIdentity, long>(
            ColumnFamilyIdentityComparer.Instance);
        try
        {
            var walBatchBytes = WalCodec.TransactionBatchRecordOverhead;
            command.Payload.Operations.ForEach(operation =>
            {
                commandBytesByFamily[operation.Family] = checked(
                    commandBytesByFamily.GetValueOrDefault(operation.Family) +
                    CoalescedCommitApplyPreflight.EstimateOperationBytes(operation));
                walBatchBytes = checked(walBatchBytes + WalCodec.MeasureTransactionBatchOperation(
                    operation.Key.Length,
                    operation.Value?.Length,
                    operation.EndExclusive?.Length));
            });
            if (NeedsPriorStateValidation(command) &&
                stagedCommands is not null &&
                stagedCommands.Any(staged => ConflictsWithEarlierWrites(command, staged)))
            {
                return false;
            }

            // A batch that cannot be framed fails when it is encoded. Committing it alone keeps
            // that failure from rolling back the well-formed commits it would be grouped with.
            if (durability != PantsDurability.BestEffort &&
                walBatchBytes > DiskFormat.WalMaximumRecordBytes)
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

    static bool NeedsPriorStateValidation(CommitRuntimeCommand command)
    {
        if (command.Payload.ConflictPolicy == PantsConflictPolicy.AbortOnWriteConflict ||
            command.Payload.Asserts.Values.Any(static assertions => assertions.Count != 0))
        {
            return true;
        }

        var containsInsert = false;
        command.Payload.Operations.ForEach(operation => containsInsert |= operation.InsertOnly);
        return containsInsert;
    }

    static bool ConflictsWithEarlierWrites(
        CommitRuntimeCommand candidate,
        CommitRuntimeCommand earlier)
    {
        var conflicts = false;
        earlier.Payload.Operations.ForEach(earlierOperation =>
        {
            candidate.Payload.Operations.ForEach(candidateOperation =>
            {
                conflicts |= OperationsOverlap(earlierOperation, candidateOperation);
            });
            if (candidate.Payload.Asserts.TryGetValue(earlierOperation.Family, out var assertions))
            {
                conflicts |= assertions.Any(assertion =>
                    OperationContainsKey(earlierOperation, assertion.Key));
            }
        });
        return conflicts;
    }

    static bool OperationsOverlap(
        TransactionIntentOperation left,
        TransactionIntentOperation right)
    {
        if (!ColumnFamilyIdentityComparer.Instance.Equals(left.Family, right.Family))
        {
            return false;
        }

        var leftRange = left.Kind == CommitOperationKind.DeleteRange;
        var rightRange = right.Kind == CommitOperationKind.DeleteRange;
        if (!leftRange && !rightRange)
        {
            return left.Key.AsSpan().SequenceEqual(right.Key);
        }

        if (leftRange && rightRange)
        {
            return left.Key.AsSpan().SequenceCompareTo(right.EndExclusive) < 0 &&
                   right.Key.AsSpan().SequenceCompareTo(left.EndExclusive) < 0;
        }

        var range = leftRange ? left : right;
        var point = leftRange ? right : left;
        return OperationContainsKey(range, point.Key);
    }

    static bool OperationContainsKey(TransactionIntentOperation operation, byte[] key)
    {
        if (operation.Kind != CommitOperationKind.DeleteRange)
        {
            return operation.Key.AsSpan().SequenceEqual(key);
        }

        return operation.Key.AsSpan().SequenceCompareTo(key) <= 0 &&
               key.AsSpan().SequenceCompareTo(operation.EndExclusive) < 0;
    }

    public static IReadOnlyList<PreparedCoalescedCommit> CreatePreparedCommits(
        RuntimeState state,
        IReadOnlyList<CommitRuntimeCommand> commands,
        ulong? sequenceFloor = null) =>
        CoalescedCommitApplyPreflight.Create(state, commands, sequenceFloor);

    public async ValueTask AppendAsync(
        RuntimeState state,
        IReadOnlyList<PreparedCoalescedCommit> prepared,
        PantsDurability durability,
        Failpoint beforeSync)
    {
        if (prepared.Count == 0 ||
            durability is not (PantsDurability.Sync or
                PantsDurability.Buffered or
                PantsDurability.BestEffort or
                PantsDurability.CloudAsync) ||
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
        var walDurability = durability == PantsDurability.CloudAsync
            ? PantsDurability.Buffered
            : durability;
        var deadline = prepared[0].Command.Deadline;
        for (var index = 1; index < prepared.Count; index++)
        {
            var candidate = prepared[index].Command.Deadline;
            if (candidate.Remaining < deadline.Remaining)
            {
                deadline = candidate;
            }
        }

        var result = await appendGroupAsync(group, state.Sequence, walDurability, beforeSync, deadline)
            .ConfigureAwait(false);
        if (result.CommitSequence != prepared[^1].Sequence)
        {
            throw new PantsInternalException("The WAL receipt does not match its admitted commit group.");
        }
        if (durability == PantsDurability.Sync)
        {
            telemetry.RecordDurabilityWaitersFannedOut(result.CommitCount);
        }
    }

    public void Apply(
        RuntimeState state,
        IReadOnlyList<PreparedCoalescedCommit> prepared)
    {
        var staged = new StagedMemtableApply(state);
        foreach (var commit in prepared)
        {
            foreach (var operation in commit.Operations)
            {
                staged.Stage(operation, commit.Sequence);
                afterOperationStaged();
            }
        }

        if (prepared.Count != 0)
        {
            staged.Publish(prepared[^1].Sequence);
        }
    }
}
