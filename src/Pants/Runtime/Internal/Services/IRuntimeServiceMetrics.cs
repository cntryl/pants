namespace Cntryl.Pants.Runtime.Internal.Services;

interface IRuntimeServiceMetrics
{
    int QueueDepth { get; }

    int InFlight { get; }

    int Outstanding { get; }

    long Enqueued { get; }

    long Completed { get; }

    long Failures { get; }
}
