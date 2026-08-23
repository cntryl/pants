namespace Cntryl.Pants.Runtime.Internal.Services.Flush;

sealed record FlushAllRuntimeRequest(
    PantsRuntimeState State) : FlushRuntimeRequest;
