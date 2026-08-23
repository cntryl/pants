namespace Cntryl.Pants;

sealed class CloudWalSealController
{
    readonly PantsCloudWritePolicy _policy;
    readonly TimeProvider _timeProvider;
    long _lastSealTimestamp;

    public CloudWalSealController(PantsCloudWritePolicy policy, TimeProvider timeProvider)
    {
        _policy = policy;
        _timeProvider = timeProvider;
        _lastSealTimestamp = timeProvider.GetTimestamp();
    }

    public int PendingWrites { get; private set; }

    public TimeSpan? RemainingDelay
    {
        get
        {
            if (PendingWrites == 0)
            {
                return null;
            }

            var elapsed = _timeProvider.GetElapsedTime(_lastSealTimestamp);
            return elapsed >= _policy.WalSealMaximumFlushDelay
                ? TimeSpan.Zero
                : _policy.WalSealMaximumFlushDelay - elapsed;
        }
    }

    public void RecordWrite(int physicalRecords = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(physicalRecords);
        PendingWrites = checked(PendingWrites + physicalRecords);
    }

    public bool ShouldSeal(long activeWalBytes) =>
        PendingWrites > 0 &&
        (activeWalBytes >= _policy.WalSealMinimumSegmentBytes ||
            PendingWrites >= _policy.WalSealMaximumPendingWrites ||
            RemainingDelay == TimeSpan.Zero);

    public void RecordSeal()
    {
        PendingWrites = 0;
        _lastSealTimestamp = _timeProvider.GetTimestamp();
    }
}
