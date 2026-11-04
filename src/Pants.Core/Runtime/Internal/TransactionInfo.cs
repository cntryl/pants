namespace Cntryl.Pants.Runtime.Internal;

sealed class TransactionInfo : ISnapshotPin
{
    public TransactionInfo(
        long transactionId,
        PantsTransactionMode mode,
        ulong beginSequence,
        DateTimeOffset startedAtUtc,
        DatabaseVersion snapshot)
    {
        TransactionId = transactionId;
        Mode = mode;
        BeginSequence = beginSequence;
        StartedAtUtc = startedAtUtc;
        StartSnapshot = snapshot;
    }

    public long TransactionId { get; }

    public PantsTransactionMode Mode { get; }

    public long SnapshotId => TransactionId;

    public ulong BeginSequence { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DatabaseVersion StartSnapshot { get; }
}
