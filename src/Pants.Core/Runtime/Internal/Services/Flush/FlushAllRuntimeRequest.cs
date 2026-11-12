namespace Cntryl.Pants.Runtime.Internal.Services.Flush;

sealed record FlushAllRuntimeRequest(
    MetadataOrigin Origin) : FlushRuntimeRequest;
