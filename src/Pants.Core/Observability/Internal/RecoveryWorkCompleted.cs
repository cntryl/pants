namespace Cntryl.Pants.Observability.Internal;

readonly record struct RecoveryWorkCompleted(string Stage, long Bytes, long Frames, long Operations)
{
    public bool HasCompletedWork => Bytes > 0 || Frames > 0 || Operations > 0;
}
