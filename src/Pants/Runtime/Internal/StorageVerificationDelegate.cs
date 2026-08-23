namespace Cntryl.Pants.Runtime.Internal;

delegate ValueTask<PantsStorageVerificationReport> StorageVerificationDelegate(
    string path,
    CancellationToken cancellationToken);
