namespace Cntryl.Pants.Runtime.Internal;

delegate ValueTask<PantsStorageVerificationReport> PantsStorageVerificationDelegate(
    string path,
    CancellationToken cancellationToken);
