namespace Cntryl.Pants.Cloud.Internal;

/// <summary>
///     The transient memory an immutable file publication needs, mirroring Midge's
///     <c>publish_immutable_file</c> admission: the local copy plus provider transport and readback
///     copies (<see cref="CopyFactor" />), and a fixed workspace for bounded range buffers.
/// </summary>
static class ImmutablePublicationEnvelope
{
    public const int CopyFactor = 4;
    public const long FixedWorkspaceBytes = 256 * 1024;

    public static long For(long sizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        return sizeBytes > (long.MaxValue - FixedWorkspaceBytes) / CopyFactor
            ? long.MaxValue
            : sizeBytes * CopyFactor + FixedWorkspaceBytes;
    }

    /// <summary>
    ///     The largest compaction partition whose publication fits a quarter of the variable pool,
    ///     leaving three quarters for live merge readers, the current writer and one indivisible key.
    /// </summary>
    public static long PartitionTarget(long poolBytes) =>
        Math.Max(1, Math.Max(0, poolBytes - FixedWorkspaceBytes) / (CopyFactor * 4));
}
