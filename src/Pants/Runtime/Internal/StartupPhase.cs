namespace Cntryl.Pants.Runtime.Internal;

enum StartupPhase
{
    Lease,
    Format,
    ManifestSnapshot,
    ManifestJournal,
    IntentReconciliation,
    WalReplay,
    SstHydration,
    CloudControlHydration,
    VersionConstruction,
    ServiceStartup
}
