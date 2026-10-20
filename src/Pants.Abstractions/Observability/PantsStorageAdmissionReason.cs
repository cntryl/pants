namespace Cntryl.Pants.Observability;

/// <summary>Why a local-storage operation could not reserve its working space.</summary>
public enum PantsStorageAdmissionReason
{
    LocalCapacity,
    CloudUpload,
    Compaction
}
