using Microsoft.Win32.SafeHandles;

namespace Cntryl.Pants.Storage.Internal.Accounting;

sealed class MetadataIoObservation(MetadataOperationLedger ledger, MetadataPayload payload)
{
    public static T Run<T>(MetadataIoObservation? observation, Func<T> operation)
    {
        observation?.Mutation();
        try
        {
            return operation();
        }
        finally
        {
            observation?.Mutation();
        }
    }

    public static void Run(MetadataIoObservation? observation, Action operation)
    {
        observation?.Mutation();
        try
        {
            operation();
        }
        finally
        {
            observation?.Mutation();
        }
    }

    public void Issued(ulong bytes) => ledger.Add((MetadataCounter)((int)MetadataCounter.SnapshotIssuedBytes + (int)payload), bytes);
    public void Returned(ulong bytes) => ledger.Add((MetadataCounter)((int)MetadataCounter.SnapshotReturnedBytes + (int)payload), bytes);
    public void Mutation() => ledger.Mutation();

    public async ValueTask WriteAsync(SafeFileHandle handle, ReadOnlyMemory<byte> bytes, long offset,
        CancellationToken cancellationToken = default)
    {
        Issued((ulong)bytes.Length);
        try
        {
            await RandomAccess.WriteAsync(handle, bytes, offset, cancellationToken).ConfigureAwait(false);
            Returned((ulong)bytes.Length);
        }
        catch
        {
            Mutation();
            throw;
        }
    }

    public void Mutate(Action operation)
    {
        Mutation();
        try
        {
            operation();
        }
        finally
        {
            Mutation();
        }
    }
}
