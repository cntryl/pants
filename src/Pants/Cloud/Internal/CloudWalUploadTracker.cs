namespace Cntryl.Pants.Cloud.Internal;

sealed class CloudWalUploadTracker(RuntimeTelemetry telemetry)
{
    readonly Lock _gate = new();
    readonly HashSet<ulong> _segments = [];
    int _count;

    public int Count => Volatile.Read(ref _count);

    public bool Admit(SealedWalSegment segment)
    {
        lock (_gate)
        {
            if (!_segments.Add(segment.SegmentId))
            {
                return false;
            }

            Volatile.Write(ref _count, checked(_count + 1));
            telemetry.RecordCloudUploadPending();
            return true;
        }
    }

    public bool Complete(SealedWalSegment segment)
    {
        lock (_gate)
        {
            if (!_segments.Remove(segment.SegmentId))
            {
                return false;
            }

            Volatile.Write(ref _count, checked(_count - 1));
            telemetry.RecordCloudUploadCompleted();
            return true;
        }
    }
}
