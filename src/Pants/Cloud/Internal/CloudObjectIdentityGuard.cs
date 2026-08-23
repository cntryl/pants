namespace Cntryl.Pants.Cloud.Internal;

sealed record CloudObjectIdentityGuard(
    ICloudObjectStore Store,
    string ObjectKey,
    string Version);
