namespace Cntryl.Pants;

readonly record struct WritePressureWaitStatus(bool IsStalled, Task StateChanged);
