namespace Cntryl.Pants.Observability.Internal;

readonly record struct WriteStallTimingSnapshot(
    long TotalNanoseconds,
    long MaximumNanoseconds,
    long ActiveNanoseconds);
