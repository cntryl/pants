namespace Cntryl.Pants;

internal sealed class TransactionInfo : ISnapshotPin
{
    public TransactionInfo(
        long transactionId,
        PantsTransactionMode mode,
        long beginSequence,
        DateTimeOffset startedAtUtc,
        DatabaseSnapshot snapshot)
    {
        TransactionId = transactionId;
        Mode = mode;
        BeginSequence = beginSequence;
        StartedAtUtc = startedAtUtc;
        StartSnapshot = snapshot;
    }

    public long TransactionId { get; }

    public long SnapshotId => TransactionId;

    public PantsTransactionMode Mode { get; }

    public long BeginSequence { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DatabaseSnapshot StartSnapshot { get; }
}
