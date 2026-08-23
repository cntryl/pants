namespace Cntryl.Pants.Runtime.Internal.Services.Wal;

sealed record FlushWalDurabilityRuntimeRequest(
    Failpoint? BeforeBoundary) : WalRuntimeRequest;
