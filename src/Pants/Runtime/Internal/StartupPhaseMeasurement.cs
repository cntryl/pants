namespace Cntryl.Pants.Runtime.Internal;

readonly record struct StartupPhaseMeasurement(
    StartupPhase Phase,
    TimeSpan Elapsed,
    long AllocatedBytes);
