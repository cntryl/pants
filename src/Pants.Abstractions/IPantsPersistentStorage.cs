namespace Cntryl.Pants;

public interface IPantsPersistentStorage
{
    bool IsPrimaryLeaseHealthy { get; }

    ValueTask<PantsStorageVerificationReport> VerifyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
