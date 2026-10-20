using System.Diagnostics.CodeAnalysis;

namespace Cntryl.Pants.Storage.Internal.Cache;

sealed class SstReaderHandleBudget
{
    internal const int DefaultLimit = 256;

    int _current;

    public SstReaderHandleBudget(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        Limit = limit;
    }

    public int Limit { get; }

    public int Current => Volatile.Read(ref _current);

    public bool TryReserve([NotNullWhen(true)] out IDisposable? reservation)
    {
        while (true)
        {
            var before = Volatile.Read(ref _current);
            if (before >= Limit)
            {
                reservation = null;
                return false;
            }

            if (Interlocked.CompareExchange(ref _current, before + 1, before) == before)
            {
                try
                {
                    reservation = new Reservation(this);
                    return true;
                }
                catch
                {
                    Release();
                    throw;
                }
            }
        }
    }

    void Release() => Interlocked.Decrement(ref _current);

    sealed class Reservation(SstReaderHandleBudget budget) : IDisposable
    {
        int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                budget.Release();
            }
        }
    }
}
