namespace Cntryl.Pants.Runtime.Internal;

readonly record struct WritePressureWaitStatus(bool IsStalled, Task StateChanged);
