namespace Cntryl.Pants.Runtime.Internal;

readonly record struct CommitCompletion(ulong Sequence, bool WriteStalled);
