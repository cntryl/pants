namespace Cntryl.Pants;

public interface IPantsCloudDatabase
{
    bool IsPrimaryLeaseHealthy { get; }

    /// <summary>Returns the highest sequence confirmed durable by cloud authority.</summary>
    /// <remarks>Commits at or below this cut survive loss of the local cache.</remarks>
    ValueTask<ulong> GetDurableSequenceAsync(CancellationToken cancellationToken = default);

    /// <summary>Waits for cloud authority to confirm the requested commit sequence.</summary>
    /// <remarks>
    ///     May seal the covering WAL early. Returns false when the caller's timeout or runtime
    ///     confirmation budget expires, or publication reports a transient failure or backpressure.
    ///     Accepted publication remains runtime-owned and a later wait may succeed. Lease loss
    ///     throws <see cref="PantsFencedException"/>, including for an already covered sequence.
    /// </remarks>
    ValueTask<bool> WaitForDurableAsync(
        ulong sequence,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
