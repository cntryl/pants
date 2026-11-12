namespace Cntryl.Pants.Support;

/// <summary>Acts as the runtime owner for tests that drive the storage component directly.</summary>
static class LocalDiskStoreCommitExtensions
{
    public static WalCommitResult AppendCommit(
        this LocalDiskStore store,
        CommitPayload payload,
        RuntimeState state,
        PantsDurability durability,
        WalMetricsRecorder? metrics = null)
    {
        try
        {
            var receipt = store.AppendCommit(payload, state.Sequence, durability, metrics);
            state.Sequence = receipt.CommitSequence;
            return receipt;
        }
        catch
        {
            if (store.IsWalFenced)
            {
                state.Health = PantsEngineHealth.Degraded;
            }

            throw;
        }
    }

    public static WalCommitGroupResult AppendCommitGroup(
        this LocalDiskStore store,
        IReadOnlyList<WalCommitGroupEntry> commits,
        RuntimeState state,
        PantsDurability durability,
        Action beforeSync,
        WalMetricsRecorder? metrics = null)
    {
        try
        {
            var receipt = store.AppendCommitGroup(commits, state.Sequence, durability, beforeSync, metrics);
            state.Sequence = receipt.CommitSequence;
            return receipt;
        }
        catch
        {
            if (store.IsWalFenced)
            {
                state.Health = PantsEngineHealth.Degraded;
            }

            throw;
        }
    }
}
