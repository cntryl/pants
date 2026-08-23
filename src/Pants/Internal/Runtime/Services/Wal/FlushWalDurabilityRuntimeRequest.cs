namespace Pants;

sealed record FlushWalDurabilityRuntimeRequest(
    PantsFailpoint? BeforeBoundary) : WalRuntimeRequest;
