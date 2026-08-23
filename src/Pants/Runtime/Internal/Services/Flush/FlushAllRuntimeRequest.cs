namespace Pants;

sealed record FlushAllRuntimeRequest(
    PantsRuntimeState State) : FlushRuntimeRequest;
