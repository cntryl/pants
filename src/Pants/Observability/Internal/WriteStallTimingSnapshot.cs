namespace Cntryl.Pants;

readonly record struct WriteStallTimingSnapshot(
    long TotalNanoseconds,
    long MaximumNanoseconds,
    long ActiveNanoseconds);
