namespace Cntryl.Pants.Runtime.Internal.Services.Flush;

sealed record FlushAllRuntimeRequest(
    RuntimeState State) : FlushRuntimeRequest;
