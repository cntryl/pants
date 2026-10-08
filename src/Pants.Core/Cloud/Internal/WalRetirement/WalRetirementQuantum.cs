namespace Cntryl.Pants.Cloud.Internal.WalRetirement;

/// <summary>
///     The work allowance of one retirement turn. A turn always makes at least one acknowledged
///     step, then yields once the allowance has elapsed. Provider calls are never cut short: the
///     turn yields only between them.
/// </summary>
sealed class WalRetirementQuantum
{
    readonly TimeSpan _allowance;
    readonly long _started;
    readonly TimeProvider _timeProvider;
    bool _progressed;

    public WalRetirementQuantum(TimeProvider timeProvider, TimeSpan allowance)
    {
        _timeProvider = timeProvider;
        _allowance = allowance;
        _started = timeProvider.GetTimestamp();
    }

    public bool ShouldYield =>
        _progressed && _timeProvider.GetElapsedTime(_started) >= _allowance;

    public void RecordProgress() => _progressed = true;
}
