namespace Cntryl.Pants;

internal delegate ValueTask<PantsStorageVerificationReport> PantsStorageVerificationDelegate(
    string path,
    CancellationToken cancellationToken);
