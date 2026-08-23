namespace Cntryl.Pants;

sealed record FlushAllRuntimeRequest(
    PantsRuntimeState State) : FlushRuntimeRequest;
