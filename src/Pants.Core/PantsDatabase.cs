using Cntryl.Pants.Storage.Internal.Lease;

namespace Cntryl.Pants;

public static class PantsDatabase
{
    public static async ValueTask<IPantsDatabase> OpenAsync(
        PantsOpenOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        return await DatabaseInstance.OpenAsync(
                options,
                RuntimeDependencies.Default,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async ValueTask<IPantsDatabase> OpenForTestingAsync(
        PantsOpenOptions options,
        RuntimeDependencies dependencies,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dependencies);
        cancellationToken.ThrowIfCancellationRequested();
        return await DatabaseInstance.OpenAsync(options, dependencies, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Explicit operator recovery for a <c>.midge_leader.lock</c> left by a writer killed while
    ///     holding it. Run it only while no writer is running against <paramref name="path" />. It
    ///     removes the lock only when the leader record is absent or older than the lease TTL plus
    ///     clock skew, and only when the lock still carries the owner token it was checked against.
    ///     Use the same lease configuration as the writers. Returns <see langword="true" /> when a
    ///     lock was removed and <see langword="false" /> when none existed.
    /// </summary>
    public static ValueTask<bool> RecoverStaleLeaseMutationLockAsync(
        string path,
        PantsLeaseConfiguration? leaseConfiguration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        var lease = leaseConfiguration ?? PantsLeaseConfiguration.Default;
        var recovered = LeaseMutationLockRecovery.Recover(
            path,
            lease.TimeToLive,
            lease.ClockSkewTolerance,
            SystemPantsClock.Instance);
        return ValueTask.FromResult(recovered);
    }

    public static ValueTask<PantsStorageVerificationReport> VerifyPathAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        StorageVerifier.VerifyPathAsync(path, cancellationToken);

    /// <summary>
    ///     Verifies a local storage directory like <see cref="VerifyPathAsync" />, but classifies
    ///     every result into an operator outcome instead of throwing. A missing, inaccessible,
    ///     non-directory or malformed path is reported through
    ///     <see cref="PantsStorageVerificationOutcome.PathFailure" />. Only cancellation throws.
    /// </summary>
    public static ValueTask<PantsStorageVerificationOutcome> VerifyPathOutcomeAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        StorageVerificationOutcomeResolver.VerifyPathAsync(path, cancellationToken);
}
