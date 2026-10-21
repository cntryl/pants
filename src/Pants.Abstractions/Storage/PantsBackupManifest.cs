namespace Cntryl.Pants.Storage;

public sealed record PantsBackupManifest(
    uint BackupVersion,
    string BackupId,
    string CapturedAt,
    ulong DurabilityFrontier,
    uint DatabaseFormatVersion,
    string EngineVersion,
    PantsBackupStorageKind StorageKind,
    IReadOnlyList<PantsBackupObject> Objects);

public sealed record PantsBackupObject(
    string Path,
    ulong SizeBytes,
    uint Crc32C);

public enum PantsBackupStorageKind
{
    Local,
    CloudSimulated
}
