namespace Pants;

sealed record CloudObjectIdentityGuard(
    ICloudObjectStore Store,
    string ObjectKey,
    string Version);
