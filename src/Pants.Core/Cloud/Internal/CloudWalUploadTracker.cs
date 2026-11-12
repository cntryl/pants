namespace Cntryl.Pants.Cloud.Internal;

sealed class CloudWalUploadTracker(RuntimeTelemetry telemetry)
{
    readonly Lock _gate = new();
    readonly Dictionary<ulong, ulong> _segments = [];
    int _count;

    public int Count => Volatile.Read(ref _count);

    public bool Covers(ulong sequence, ulong openFirstSequence)
    {
        lock (_gate)
        {
            // The newest inflight predecessor also covers reserved holes below the
            // actual open segment once the contiguous publication frontier reaches it.
            return _segments.Values.Any(maximum => maximum >= sequence) ||
                   sequence < openFirstSequence && _segments.Count != 0;
        }
    }

    public bool Admit(SealedWalSegment segment)
    {
        lock (_gate)
        {
            if (!_segments.TryAdd(segment.SegmentId, segment.MaximumSequence))
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
