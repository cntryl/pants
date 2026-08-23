namespace Cntryl.Pants.Storage;

public sealed record PantsStorageVerificationReport(
    long ManifestEpoch,
    int ManifestFilesVerified,
    int SstFilesVerified,
    long BytesVerified,
    long DataBlocksVerified,
    long? WalBoundary,
    long WalRecoveryRecordsReplayed,
    long WalRecoveryBytesReplayed,
    int IntentEntriesLoaded,
    bool Authoritative,
    PantsEngineHealth Health,
    IReadOnlyList<string> Warnings);
