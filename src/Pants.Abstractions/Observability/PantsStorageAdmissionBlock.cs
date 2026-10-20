namespace Cntryl.Pants.Observability;

/// <summary>
/// The oldest rejected local-storage admission that has not since succeeded for its operation.
/// </summary>
public sealed record PantsStorageAdmissionBlock
{
    public PantsStorageAdmissionKind Operation { get; init; }

    public PantsStorageAdmissionReason Reason { get; init; }

    public long RequestedBytes { get; init; }

    public long FreeBytesAtRejection { get; init; }

    public long AgeMilliseconds { get; init; }

    public long Attempts { get; init; }
}
