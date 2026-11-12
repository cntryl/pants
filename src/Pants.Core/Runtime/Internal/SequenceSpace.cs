namespace Cntryl.Pants.Runtime.Internal;

static class SequenceSpace
{
    const string ExhaustedMessage = "The transaction sequence space is exhausted.";

    /// <summary>
    ///     Computes the commit sequence of a transaction with <paramref name="operationCount" />
    ///     operations that follows <paramref name="current" />, without mutating any state.
    /// </summary>
    public static ulong CommitSequenceAfter(ulong current, ulong operationCount)
    {
        // A transaction consumes begin and commit boundaries as well as its mutations.
        // u64::MAX is the persisted dropped-family frontier, never a live sequence.
        if (operationCount > ulong.MaxValue - 2 ||
            current >= ulong.MaxValue - operationCount - 2)
        {
            throw new PantsResourceLimitException(ExhaustedMessage);
        }

        return current + operationCount + 2;
    }

    public static ulong EpochReservationThrough(ulong current, ulong block)
    {
        if (current > ulong.MaxValue - block)
        {
            throw new PantsResourceLimitException(ExhaustedMessage);
        }

        return current + block;
    }

    public static ulong FirstAfter(ulong current) => current == ulong.MaxValue
        ? throw new PantsResourceLimitException(ExhaustedMessage)
        : current + 1;
}
