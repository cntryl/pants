namespace Cntryl.Pants.Storage;

/// <summary>Optional caller admission slowdown and a shared maintenance output rate.</summary>
/// <param name="L0SoftLimit">Published and reserved L0 generations that trigger slowdown.</param>
/// <param name="MemorySoftLimitBytes">Total admitted memtable bytes that trigger slowdown.</param>
/// <param name="PendingCompactionSoftLimitBytes">Estimated level excess bytes that trigger slowdown.</param>
/// <param name="PendingCompactionHardLimitBytes">Estimated level excess bytes that reject mutations.</param>
/// <param name="MaximumAdmissionDelay">One maximum caller delay within the original response deadline.</param>
/// <param name="BackgroundWriteBytesPerSecond">Shared encoded flush, compaction, and repair output rate.</param>
/// <remarks>
///     Null limits disable their controls. Soft limits must be positive and below their resolved hard
///     limits and require a positive delay. Hard pressure and shutdown bypass the maintenance rate
///     so that owned physical work can drain. The default policy preserves immediate admission.
/// </remarks>
public sealed record PantsWriteBackpressureConfiguration(
    int? L0SoftLimit = null,
    long? MemorySoftLimitBytes = null,
    ulong? PendingCompactionSoftLimitBytes = null,
    ulong? PendingCompactionHardLimitBytes = null,
    TimeSpan MaximumAdmissionDelay = default,
    ulong? BackgroundWriteBytesPerSecond = null);
