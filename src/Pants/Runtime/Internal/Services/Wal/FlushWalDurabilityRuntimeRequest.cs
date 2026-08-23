namespace Cntryl.Pants;

sealed record FlushWalDurabilityRuntimeRequest(
    PantsFailpoint? BeforeBoundary) : WalRuntimeRequest;
