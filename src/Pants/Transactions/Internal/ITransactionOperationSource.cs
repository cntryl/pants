namespace Pants;

internal interface ITransactionOperationSource
{
    ulong Count { get; }

    bool IsSpilled { get; }

    void Validate();

    void ForEach(Action<TransactionIntentOperation> visitor);

    TransactionIntentLookup? LatestBefore(ulong ordinal, ReadOnlySpan<byte> key);
}
