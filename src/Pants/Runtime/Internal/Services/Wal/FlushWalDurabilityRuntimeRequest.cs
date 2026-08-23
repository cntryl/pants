namespace Cntryl.Pants.Runtime.Internal.Services.Wal;

sealed record FlushWalDurabilityRuntimeRequest(
    PantsFailpoint? BeforeBoundary) : WalRuntimeRequest;
