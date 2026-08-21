namespace Pants;

internal sealed class TransactionMemoryPool
{
    private readonly long _capacity;
    private long _used;

    public TransactionMemoryPool(long capacity)
    {
        _capacity = capacity;
    }

    public long Used => Volatile.Read(ref _used);

    public bool TryReserve(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        while (true)
        {
            long observed = Volatile.Read(ref _used);
            if (bytes > _capacity - observed)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _used, observed + bytes, observed) == observed)
            {
                return true;
            }
        }
    }

    public void Release(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        long remaining = Interlocked.Add(ref _used, -bytes);
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _used, 0);
            throw new InvalidOperationException("Transaction memory accounting underflowed.");
        }
    }
}
