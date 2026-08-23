namespace Cntryl.Pants;

sealed class WalRotationRecoveryException : Exception
{
    public WalRotationRecoveryException(
        Exception rotationFailure,
        Exception recoveryFailure)
        : base(
            "The WAL stream could not be recovered after rotation failed.",
            new AggregateException(rotationFailure, recoveryFailure))
    {
        RotationFailure = rotationFailure;
        RecoveryFailure = recoveryFailure;
    }

    public Exception RotationFailure { get; }

    public Exception RecoveryFailure { get; }
}
