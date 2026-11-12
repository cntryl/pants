namespace Cntryl.Pants.Storage;

public sealed record PantsStorageVerificationReport(
    ulong ManifestEpoch,
    int ManifestFilesVerified,
    int SstFilesVerified,
    long BytesVerified,
    long DataBlocksVerified,
    ulong? WalBoundary,
    long WalRecoveryRecordsReplayed,
    long WalRecoveryBytesReplayed,
    int IntentEntriesLoaded,
    bool Authoritative,
    PantsEngineHealth Health,
    IReadOnlyList<string> Warnings);
