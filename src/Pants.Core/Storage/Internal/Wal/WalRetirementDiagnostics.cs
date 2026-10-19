using Cntryl.Pants.Observability;

namespace Cntryl.Pants.Storage.Internal.Wal;

sealed class WalRetirementDiagnostics
{
    int _blockedCause;

    public PantsWalRetirementBlockedCause BlockedCause =>
        (PantsWalRetirementBlockedCause)Volatile.Read(ref _blockedCause);

    public void SetBlockedCause(PantsWalRetirementBlockedCause cause) =>
        Volatile.Write(ref _blockedCause, (int)cause);
}
