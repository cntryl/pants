namespace Cntryl.Pants.Runtime.Internal;

static class SequenceSpace
{
    const string ExhaustedMessage = "The transaction sequence space is exhausted.";

    /// <summary>
    ///     Computes the commit sequence of a transaction with <paramref name="operationCount" />
    ///     operations that follows <paramref name="current" />, without mutating any state.
    /// </summary>
    public static long CommitSequenceAfter(ulong current, ulong operationCount)
    {
        if (current >= long.MaxValue ||
            operationCount >= long.MaxValue ||
            current + operationCount + 1 >= long.MaxValue)
        {
            throw new PantsResourceLimitException(ExhaustedMessage);
        }

        return (long)(current + operationCount + 1) + 1;
    }

    public static long NextMemorySequence(long current) => current == long.MaxValue
        ? throw new PantsResourceLimitException(ExhaustedMessage)
        : current + 1;
}
