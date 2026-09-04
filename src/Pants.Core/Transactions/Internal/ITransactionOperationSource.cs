namespace Cntryl.Pants.Transactions.Internal;

interface ITransactionOperationSource
{
    ulong Count { get; }

    bool IsSpilled { get; }

    void Validate();

    void ForEach(Action<TransactionIntentOperation> visitor);

    ValueTask ForEachAsync(
        Func<TransactionIntentOperation, ValueTask> visitor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        ForEach(operation =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            visitor(operation).AsTask().GetAwaiter().GetResult();
        });
        return ValueTask.CompletedTask;
    }

    TransactionIntentLookup? LatestBefore(ulong ordinal, ReadOnlySpan<byte> key);
}
